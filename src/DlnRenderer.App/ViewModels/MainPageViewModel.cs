using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DlnRenderer.Core.Ipc;
using DlnRenderer.Core.Models;
using DlnRenderer.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DlnRenderer.ViewModels;

/// <summary>
/// Projects the renderer service state onto bindable properties. The UI owns no player: playback,
/// DLNA discovery and video output all live in the service process, so closing the window does not
/// interrupt playback.
/// </summary>
/// <remarks>
/// Snapshots arrive on the pipe read thread and are marshalled through the captured dispatcher.
/// Commands are fire-and-forget through the shared <see cref="App.Client"/>; the resulting state
/// comes back as a pushed snapshot, so the UI never guesses what happened.
/// </remarks>
public sealed class MainPageViewModel : ObservableObject, IDisposable
{
    /// <summary>How often a disconnected page retries the service.</summary>
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);

    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly ServiceClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private int _reconnectRunning;
    private bool _disposed;

    private string _serviceText = "正在连接后台服务…";
    private string _statusText = "待机";
    private string _detailText = "等待 DLNA 控制端推送媒体";
    private string _titleText = "未加载媒体";
    private string _artistText = string.Empty;
    private string _statusGlyph = "\uE71A";
    private string _positionText = "00:00";
    private string _durationText = "00:00";
    private double _positionSeconds;
    private double _durationSeconds;
    private double _volume = 100;
    private bool _muted;
    private bool _hasError;
    private string _errorText = string.Empty;
    private bool _hasMedia;
    private bool _isPlaying;
    private bool _showsVideo;
    private bool _isConnected;
    private bool _serviceBuildMismatch;
    private ImageSource? _coverImage;
    private Uri? _coverUri;

    public MainPageViewModel()
    {
        // Shared with the tray icon and the settings page: one pipe, one connection.
        _client = App.Client;
        _client.SnapshotChanged += OnSnapshotChanged;
        _client.Faulted += OnClientFaulted;
        _client.RequestRejected += OnRequestRejected;
    }

    /// <summary>A refused command is shown, but it does not change the connection state.</summary>
    private void OnRequestRejected(object? sender, string reason)
        => _dispatcher.TryEnqueue(() => ReportError(reason));

    public string ServiceText { get => _serviceText; private set => SetProperty(ref _serviceText, value); }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public string DetailText { get => _detailText; private set => SetProperty(ref _detailText, value); }

    public string TitleText { get => _titleText; private set => SetProperty(ref _titleText, value); }

    /// <summary>Artist and album, joined only when the control point supplied them.</summary>
    public string ArtistText { get => _artistText; private set => SetProperty(ref _artistText, value); }

    /// <summary>Segoe Fluent glyph matching the transport state.</summary>
    public string StatusGlyph { get => _statusGlyph; private set => SetProperty(ref _statusGlyph, value); }

    public string PositionText { get => _positionText; private set => SetProperty(ref _positionText, value); }

    public string DurationText { get => _durationText; private set => SetProperty(ref _durationText, value); }

    public double PositionSeconds { get => _positionSeconds; private set => SetProperty(ref _positionSeconds, value); }

    public double DurationSeconds { get => _durationSeconds; private set => SetProperty(ref _durationSeconds, value); }

    public double Volume { get => _volume; private set => SetProperty(ref _volume, value); }

    /// <summary>Player mute state, as reported by the service snapshot.</summary>
    public bool IsMuted { get => _muted; private set => SetProperty(ref _muted, value); }

    public bool HasError { get => _hasError; private set => SetProperty(ref _hasError, value); }

    public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }

    public bool HasMedia { get => _hasMedia; private set { if (SetProperty(ref _hasMedia, value)) { OnPropertyChanged(nameof(IsIdle)); } } }

    public bool IsIdle => !HasMedia;

    public bool IsPlaying { get => _isPlaying; private set => SetProperty(ref _isPlaying, value); }

    /// <summary>
    /// True while a video is being presented, i.e. while the renderer is drawing a picture somewhere.
    /// </summary>
    /// <remarks>
    /// Used to show and hide the floating transport bar that sits over an embedded cast. It is
    /// deliberately independent of the display mode: the mode decides where the picture goes, this
    /// decides whether there is one.
    /// </remarks>
    public bool ShowsVideo { get => _showsVideo; private set => SetProperty(ref _showsVideo, value); }

    public bool IsConnected { get => _isConnected; private set => SetProperty(ref _isConnected, value); }

    /// <summary>Cover art from the control point's DIDL metadata, null when it supplied none.</summary>
    public ImageSource? CoverImage { get => _coverImage; private set => SetProperty(ref _coverImage, value); }

    /// <summary>Connects to the service, starting it when it is not already running.</summary>
    public async Task InitializeAsync()
    {
        var connected = await _client.ConnectAsync(startIfMissing: true).ConfigureAwait(true);
        ApplyConnection(connected);
        if (connected)
        {
            ApplySnapshot(_client.Snapshot);
        }
        else
        {
            StartReconnectLoop();
        }
    }

    public Task PlayAsync() => SendAsync(IpcCommands.Play);

    public Task PauseAsync() => SendAsync(IpcCommands.Pause);

    public Task StopAsync() => SendAsync(IpcCommands.Stop);

    public Task SeekAsync(double seconds) => SendAsync(IpcCommands.Seek, seekSeconds: Math.Max(0, seconds));

    public Task SetVolumeAsync(double volume) => SendAsync(IpcCommands.SetVolume, volume: (int)Math.Round(volume));

    public Task SetMuteAsync(bool muted) => SendAsync(IpcCommands.SetMute, muted: muted);

    /// <summary>Flips the player mute state from the current snapshot.</summary>
    public Task ToggleMuteAsync() => SetMuteAsync(!IsMuted);

    private async Task SendAsync(string command, double? seekSeconds = null, int? volume = null, bool? muted = null)
    {
        if (!IsConnected)
        {
            // Reconnect on demand: the service may have been restarted while the window was open.
            await InitializeAsync().ConfigureAwait(true);
            if (!IsConnected)
            {
                return;
            }
        }

        var snapshot = await _client.SendAsync(command, seekSeconds, volume, muted).ConfigureAwait(true);
        if (snapshot is null)
        {
            ReportError("后台服务没有响应，请检查服务是否仍在运行。");
            ApplyConnection(false);
            return;
        }

        ApplySnapshot(snapshot);
    }

    private void OnSnapshotChanged(object? sender, RendererSnapshot snapshot)
    {
        // Raised on the pipe thread: never touch XAML from here.
        _dispatcher.TryEnqueue(() => ApplySnapshot(snapshot));
    }

    private void OnClientFaulted(object? sender, string reason)
    {
        _dispatcher.TryEnqueue(() =>
        {
            ApplyConnection(false);
            ReportError(reason);
            StartReconnectLoop();
        });
    }

    /// <summary>
    /// Retries the service while the page is open, so a restarted service comes back on its own.
    /// </summary>
    /// <remarks>
    /// Only one loop runs at a time: a burst of faults (pipe dropped and a failed restart) must not
    /// spawn one retry loop per event.
    /// </remarks>
    private void StartReconnectLoop()
    {
        if (_disposed || Interlocked.Exchange(ref _reconnectRunning, 1) == 1)
        {
            return;
        }

        _ = Task.Run(ReconnectLoopAsync);
    }

    private async Task ReconnectLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(ReconnectInterval);
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (IsConnected)
                {
                    return;
                }

                bool connected;
                try
                {
                    connected = await _client.ConnectAsync(startIfMissing: true, _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (!connected)
                {
                    continue;
                }

                var snapshot = _client.Snapshot;
                _dispatcher.TryEnqueue(() =>
                {
                    ApplyConnection(true);
                    ApplySnapshot(snapshot);
                });
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectRunning, 0);
        }
    }

    private void ApplyConnection(bool connected)
    {
        IsConnected = connected;
        ServiceBuildMismatch = false;
        ServiceText = connected ? "后台服务：已连接" : "后台服务：未连接";
        OnPropertyChanged(nameof(ServiceSeverity));
    }

    /// <summary>
    /// True when the service reports a build other than the UI's, or a build old enough not to
    /// report one at all.
    /// </summary>
    /// <remarks>
    /// The two are separate executables, and a stale service silently ignores message fields it does
    /// not know: a fixed bug then looks unfixed, or behaves differently from what the code says.
    /// Saying so beats leaving the user to debug a build mismatch as if it were a defect.
    /// </remarks>
    public bool ServiceBuildMismatch { get => _serviceBuildMismatch; private set => SetProperty(ref _serviceBuildMismatch, value); }

    /// <summary>Disconnected, or talking to a different build, is a warning rather than neutral information.</summary>
    public InfoBarSeverity ServiceSeverity => IsConnected && !_serviceBuildMismatch
        ? InfoBarSeverity.Informational
        : InfoBarSeverity.Warning;

    private void ApplySnapshot(RendererSnapshot snapshot)
    {
        IsConnected = true;

        var uiBuild = typeof(MainPageViewModel).Assembly.GetName().Version?.ToString(3);
        ServiceBuildMismatch = snapshot.ServiceBuild is null
            || (uiBuild is not null && snapshot.ServiceBuild != uiBuild);

        ServiceText = _serviceBuildMismatch
            ? $"后台服务：版本不一致（服务 {snapshot.ServiceBuild ?? "旧版本"} / 界面 {uiBuild ?? "?"}）· 请在设置页重启后台服务"
            : snapshot.DiscoveryActive
                ? $"后台服务：已连接 · 局域网发现已开启（{snapshot.DeviceName ?? "DlnRenderer"}）"
                : $"后台服务：已连接 · 发现未开启（{snapshot.DeviceName ?? "DlnRenderer"}）";

        OnPropertyChanged(nameof(ServiceSeverity));

        var status = snapshot.Playback;
        var media = status.Media;

        HasMedia = media is not null;
        IsPlaying = status.State == PlaybackState.Playing;

        // Mirrors VideoWindowPolicy on the service side: the picture is on screen from the moment a
        // video starts until Stop or an error. The page uses it to decide whether the floating
        // transport bar belongs over the window.
        ShowsVideo = status.MediaKind == MediaKind.Video
            && status.State is not (PlaybackState.Stopped or PlaybackState.Error);

        (StatusText, StatusGlyph) = status.State switch
        {
            PlaybackState.Playing => ("播放中", "\uE768"),
            PlaybackState.Paused => ("已暂停", "\uE769"),
            PlaybackState.Buffering => ("缓冲中", "\uE895"),
            PlaybackState.Transitioning => ("正在加载", "\uE895"),
            PlaybackState.Error => ("播放错误", "\uE783"),
            PlaybackState.Stopped => (HasMedia ? "已停止" : "待机", "\uE71A"),
            _ => ("待机", "\uE71A"),
        };

        TitleText = media?.Title ?? media?.MediaUri.ToString() ?? "未加载媒体";
        ArtistText = string.Join(
            " · ",
            new[] { media?.Artist, media?.Album }.Where(value => !string.IsNullOrWhiteSpace(value)));

        DetailText = status.MediaKind switch
        {
            MediaKind.Video => "视频 · 画面显示在渲染器窗口中",
            MediaKind.Audio => "音频",
            _ => snapshot.DiscoveryActive
                ? $"等待投屏 · 设备名 {snapshot.DeviceName ?? "DlnRenderer"}"
                : "等待 DLNA 控制端推送媒体",
        };

        UpdateCover(media?.CoverUri);

        PositionText = Format(status.Position);
        DurationText = status.Duration is { } duration ? Format(duration) : "00:00";
        PositionSeconds = status.Position.TotalSeconds;
        DurationSeconds = status.Duration?.TotalSeconds ?? 0;
        Volume = status.Volume;
        IsMuted = status.Muted;

        if (status.State == PlaybackState.Error)
        {
            ReportError(status.ErrorMessage ?? "播放失败。");
        }
        else
        {
            HasError = false;
            ErrorText = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LastError))
        {
            ReportError(snapshot.LastError);
        }

        DiagnosticsLog.Write(
            $"snapshot state={status.State} kind={status.MediaKind} pos={status.Position} dur={status.Duration} discovery={snapshot.DiscoveryActive}");
    }

    /// <summary>
    /// Rebuilds the cover image only when the URI actually changed, so a position tick does not
    /// recreate the bitmap every quarter second.
    /// </summary>
    private void UpdateCover(Uri? coverUri)
    {
        if (coverUri == _coverUri)
        {
            return;
        }

        _coverUri = coverUri;
        if (coverUri is null || !coverUri.IsAbsoluteUri)
        {
            CoverImage = null;
            return;
        }

        try
        {
            CoverImage = new BitmapImage(coverUri);
        }
        catch (Exception ex)
        {
            // A malformed or unsupported cover URL must not break the status display.
            DiagnosticsLog.Write($"cover art rejected ({coverUri}): {ex.Message}");
            CoverImage = null;
        }
    }

    private void ReportError(string message)
    {
        HasError = true;
        ErrorText = message;
        DiagnosticsLog.Write($"error {message}");
    }

    private static string Format(TimeSpan value)
        => value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Detaches from the shared client. The client itself is owned by <see cref="App"/> and must
    /// outlive the page, otherwise navigating to settings would drop the tray's connection.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _client.SnapshotChanged -= OnSnapshotChanged;
        _client.RequestRejected -= OnRequestRejected;
        _client.Faulted -= OnClientFaulted;

        // Cancelled but not disposed: an in-flight connection attempt may still read the token, and
        // a completed attempt would then observe ObjectDisposedException on the way out.
        _lifetime.Cancel();
    }
}
