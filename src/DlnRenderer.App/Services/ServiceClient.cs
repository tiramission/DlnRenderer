using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using DlnRenderer.Core.Ipc;
using DlnRenderer.Core.Models;

namespace DlnRenderer.Services;

/// <summary>
/// UI-side client for the renderer service. Owns the pipe connection, sends transport commands and
/// keeps the latest pushed snapshot so the view can bind to it.
/// </summary>
/// <remarks>
/// The service is a separate process by design: closing the window must not stop playback or
/// discovery. This class therefore never kills the service on dispose unless it started it.
/// All members are safe to call from the UI thread; network and pipe work is awaited.
/// </remarks>
public sealed class ServiceClient : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Idle interval between keep-alive requests.
    /// </summary>
    /// <remarks>
    /// The server drops a connection that sends nothing for 30 seconds, so a UI that is merely
    /// watching a cast would be disconnected. Must stay well below that watchdog.
    /// </remarks>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private NamedPipeClientStream? _pipe;
    private Task? _readLoop;
    private Task? _keepAliveLoop;
    private Process? _ownedService;

    public event EventHandler<RendererSnapshot>? SnapshotChanged;

    public RendererSnapshot Snapshot { get; private set; } = RendererSnapshot.Empty;

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>Raised when the connection drops, so the UI can show a clear error instead of stale data.</summary>
    public event EventHandler<string>? Faulted;

    /// <summary>
    /// Connects to the service, starting it if it is not running. Returns false when the service
    /// could not be reached; <paramref name="error"/> then carries a user-readable reason.
    /// </summary>
    public async Task<bool> ConnectAsync(bool startIfMissing, CancellationToken cancellationToken = default)
    {
        // The reconnection timer and a user command may both try to connect; serialising this keeps
        // a second pipe (and a second snapshot stream) from being opened.
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ConnectCoreAsync(startIfMissing, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task<bool> ConnectCoreAsync(bool startIfMissing, CancellationToken cancellationToken)
    {
        if (IsConnected)
        {
            return true;
        }

        if (await TryConnectAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (!startIfMissing)
        {
            return false;
        }

        if (!TryStartService(out var startError))
        {
            Faulted?.Invoke(this, startError ?? "无法启动后台服务。");
            return false;
        }

        // A fresh process needs a moment to create the pipe.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryConnectAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        Faulted?.Invoke(this, "后台服务已启动，但未能在 10 秒内建立连接。");
        return false;
    }

    public async Task<RendererSnapshot?> SendAsync(
        string command,
        double? seekSeconds = null,
        int? volume = null,
        bool? muted = null,
        VideoTarget? videoTarget = null,
        CancellationToken cancellationToken = default)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected)
        {
            return null;
        }

        var requestId = Guid.NewGuid().ToString("N");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        var acquired = false;
        try
        {
            await _sendGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;

            await IpcFraming.WriteAsync(
                pipe,
                new IpcMessage
                {
                    Type = IpcMessageTypes.Request,
                    RequestId = requestId,
                    Command = command,
                    SeekSeconds = seekSeconds,
                    Volume = volume,
                    Muted = muted,
                    VideoTarget = videoTarget,
                },
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // A cancellation caused by disposing the client is expected, not a fault to report.
            if (!_cts.IsCancellationRequested)
            {
                HandleDisconnect($"发送命令失败：{ex.Message}");
            }

            return null;
        }
        finally
        {
            if (acquired)
            {
                _sendGate.Release();
            }
        }

        // The response is observed by the read loop, which updates Snapshot; callers simply read it
        // after awaiting a short settle so the UI does not need a second channel.
        await Task.Delay(60, cancellationToken).ConfigureAwait(false);
        return Snapshot;
    }

    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = IpcFraming.CreateClientStream();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return false;
        }

        _pipe = pipe;
        _readLoop = Task.Run(() => ReadLoopAsync(pipe, _cts.Token));
        _keepAliveLoop = Task.Run(() => KeepAliveLoopAsync(_cts.Token));
        return true;
    }

    /// <summary>
    /// Keeps an idle connection open and refreshes the snapshot when no event arrived.
    /// </summary>
    /// <remarks>
    /// <see cref="IpcCommands.GetStatus"/> is used deliberately instead of a bespoke ping: it is an
    /// already validated, side-effect-free command whose response carries the current state, so a
    /// lull in pushes cannot leave the window showing stale data.
    /// </remarks>
    private async Task KeepAliveLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(KeepAliveInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!IsConnected)
                {
                    return;
                }

                await SendAsync(IpcCommands.GetStatus, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await IpcFraming.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                if (message.Snapshot is { } snapshot)
                {
                    Snapshot = snapshot;
                    SnapshotChanged?.Invoke(this, snapshot);
                }
            }

            HandleDisconnect("后台服务已断开连接。");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or ObjectDisposedException)
        {
            HandleDisconnect($"与后台服务的连接已中断：{ex.Message}");
        }
    }

    private void HandleDisconnect(string reason)
    {
        if (IsConnected)
        {
            _pipe = null;
            Faulted?.Invoke(this, reason);
        }
        else
        {
            Faulted?.Invoke(this, reason);
        }
    }

    private bool TryStartService(out string? error)
    {
        error = null;
        var servicePath = ResolveServicePath();
        if (servicePath is null || !File.Exists(servicePath))
        {
            error = "未找到后台服务可执行文件 DlnRenderer.Service.exe。请先生成解决方案。";
            return false;
        }

        try
        {
            _ownedService = Process.Start(new ProcessStartInfo(servicePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(servicePath)!,
            });

            if (_ownedService is null)
            {
                error = "无法创建后台服务进程。";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"启动后台服务失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Looks for the service next to the app first (packaged/single-folder layout) and then in the
    /// sibling service build output used during development.
    /// </summary>
    private static string? ResolveServicePath()
    {
        const string fileName = "DlnRenderer.Service.exe";
        var baseDirectory = AppContext.BaseDirectory;
        var local = Path.Combine(baseDirectory, fileName);
        if (File.Exists(local))
        {
            return local;
        }

        // Development layout: src/DlnRenderer.App/bin/<platform>/<tfm>/<rid>/ -> src/DlnRenderer.Service/bin/...
        var directory = new DirectoryInfo(baseDirectory);
        for (var i = 0; i < 8 && directory is not null; i++)
        {
            var src = Path.Combine(directory.FullName, "src");
            if (Directory.Exists(src))
            {
                var candidate = Directory
                    .EnumerateFiles(Path.Combine(src, "DlnRenderer.Service"), fileName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (candidate is not null)
                {
                    return candidate;
                }
            }

            directory = directory.Parent;
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_pipe is { } pipe)
        {
            try
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
            }

            _pipe = null;
        }

        foreach (var loop in new[] { _readLoop, _keepAliveLoop })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        // Deliberately do NOT kill _ownedService: the service outlives the UI by design.
        _ownedService?.Dispose();
        _sendGate.Dispose();
        _connectGate.Dispose();
        _cts.Dispose();
    }
}
