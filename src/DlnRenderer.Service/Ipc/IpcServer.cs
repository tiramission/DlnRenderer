using System.IO.Pipes;
using DlnRenderer.Core.Ipc;
using DlnRenderer.Service.Hosting;

namespace DlnRenderer.Service.Ipc;

/// <summary>
/// Length-prefixed JSON over a named pipe, restricted to the current user. The UI connects, issues
/// commands and receives pushed snapshots; a misbehaving client can only affect its own connection.
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    private const int MaxClients = 8;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private readonly IRendererControl _session;
    private readonly ServiceLog _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ClientConnection> _clients = [];
    private readonly Action _shutdownRequested;
    private readonly string _pipeName;
    private Task? _acceptLoop;

    public IpcServer(IRendererControl session, ServiceLog log, Action shutdownRequested, string? pipeName = null)
    {
        _session = session;
        _log = log;
        _shutdownRequested = shutdownRequested;
        _pipeName = pipeName ?? IpcProtocol.PipeName;
    }

    public int ConnectedClients
    {
        get
        {
            lock (_clients)
            {
                return _clients.Count;
            }
        }
    }

    public void Start()
    {
        _session.SnapshotChanged += Broadcast;
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _log.Write($"ipc listening on pipe '{_pipeName}' (current user only)");
    }

    public async ValueTask DisposeAsync()
    {
        _session.SnapshotChanged -= Broadcast;
        await _cts.CancelAsync().ConfigureAwait(false);

        List<ClientConnection> clients;
        lock (_clients)
        {
            clients = [.. _clients];
        }

        foreach (var client in clients)
        {
            client.Dispose();
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                // CurrentUserOnly sets an ACL that excludes every other local account.
                // The explicit buffer sizes prevent the full-duplex deadlock documented on
                // IpcProtocol.PipeBufferBytes.
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    MaxClients,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    IpcProtocol.PipeBufferBytes,
                    IpcProtocol.PipeBufferBytes);
            }
            catch (IOException ex)
            {
                _log.Write($"ipc pipe creation failed: {ex.Message}");
                return;
            }

            try
            {
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                _log.Write($"ipc accept failed: {ex.Message}");
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            var client = new ClientConnection(pipe);
            lock (_clients)
            {
                _clients.Add(client);
            }

            _log.Write($"ipc client connected ({ConnectedClients} active)");
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(ClientConnection client)
    {
        try
        {
            // Send the initial snapshot without blocking the read loop: a slow client must not be
            // able to stall requests it has already queued.
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await client.SendAsync(
                            new IpcMessage
                            {
                                Type = IpcMessageTypes.Event,
                                Snapshot = _session.GetSnapshot(),
                            },
                            _cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                    {
                    }
                },
                CancellationToken.None);

            while (!_cts.IsCancellationRequested && client.IsConnected)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(ReadTimeout);

                IpcMessage? request;
                try
                {
                    request = await IpcFraming.ReadAsync(client.Stream, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                {
                    _log.Write("ipc client read timed out; dropping it");
                    break;
                }

                if (request is null)
                {
                    break;
                }

                var response = await DispatchAsync(request).ConfigureAwait(false);
                await client.SendAsync(response, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            _log.Write($"ipc client dropped: {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            _log.Write($"ipc protocol violation: {ex.Message}");
        }
        catch (Exception ex)
        {
            _log.Write($"ipc client failed: {ex.Message}");
        }
        finally
        {
            client.Dispose();
            lock (_clients)
            {
                _clients.Remove(client);
            }

            _log.Write($"ipc client disconnected ({ConnectedClients} active)");
        }
    }

    private async Task<IpcMessage> DispatchAsync(IpcMessage request)
    {
        if (request.Type != IpcMessageTypes.Request)
        {
            return Failure(request, "only request messages are accepted");
        }

        if (!string.Equals(request.Version, IpcProtocol.Version, StringComparison.Ordinal))
        {
            return Failure(request, $"unsupported protocol version '{request.Version}'");
        }

        try
        {
            switch (request.Command)
            {
                case IpcCommands.GetStatus:
                    break;

                case IpcCommands.Play:
                    await _session.PlayAsync(_cts.Token).ConfigureAwait(false);
                    break;

                case IpcCommands.Pause:
                    await _session.PauseAsync(_cts.Token).ConfigureAwait(false);
                    break;

                case IpcCommands.Stop:
                    // Same effect as DLNA Stop: playback ends, so the overlay comes down. A paused
                    // cast is deliberately left on screen, see VideoWindowPolicy.
                    await _session.StopAsync(_cts.Token).ConfigureAwait(false);
                    break;

                case IpcCommands.Seek:
                    if (request.SeekSeconds is not { } seconds || seconds < 0 || double.IsNaN(seconds))
                    {
                        return Failure(request, "Seek requires a non-negative SeekSeconds value");
                    }

                    await _session.SeekAsync(TimeSpan.FromSeconds(seconds), _cts.Token).ConfigureAwait(false);
                    break;

                case IpcCommands.SetVolume:
                    if (request.Volume is not { } volume || volume is < 0 or > 100)
                    {
                        return Failure(request, "SetVolume requires Volume in the range 0-100");
                    }

                    await _session.SetVolumeAsync(volume, _cts.Token).ConfigureAwait(false);
                    break;

                case "Shutdown":
                    _log.Write("shutdown requested over ipc");
                    _shutdownRequested();
                    break;

                default:
                    return Failure(request, $"unsupported command '{request.Command}'");
            }

            return new IpcMessage
            {
                Type = IpcMessageTypes.Response,
                RequestId = request.RequestId,
                Command = request.Command,
                Success = true,
                Snapshot = _session.GetSnapshot(),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Write($"ipc command '{request.Command}' failed: {ex.Message}");
            return Failure(request, ex.Message);
        }
    }

    private IpcMessage Failure(IpcMessage request, string error) => new()
    {
        Type = IpcMessageTypes.Response,
        RequestId = request.RequestId,
        Command = request.Command,
        Success = false,
        Error = error,
        Snapshot = _session.GetSnapshot(),
    };

    private void Broadcast(RendererSnapshot snapshot)
    {
        List<ClientConnection> clients;
        lock (_clients)
        {
            clients = [.. _clients];
        }

        if (clients.Count == 0)
        {
            return;
        }

        var message = new IpcMessage
        {
            Type = IpcMessageTypes.Event,
            Snapshot = snapshot,
        };

        foreach (var client in clients)
        {
            // Fire-and-forget on a dedicated task: the player's event thread must not block on I/O.
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await client.SendAsync(message, _cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                    {
                    }
                },
                CancellationToken.None);
        }
    }

    private sealed class ClientConnection(NamedPipeServerStream stream) : IDisposable
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);

        public NamedPipeServerStream Stream { get; } = stream;

        public bool IsConnected => Stream.IsConnected;

        public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await IpcFraming.WriteAsync(Stream, message, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public void Dispose()
        {
            try
            {
                Stream.Dispose();
            }
            catch (IOException)
            {
            }

            _writeGate.Dispose();
        }
    }
}
