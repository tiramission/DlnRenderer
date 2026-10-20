using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DlnRenderer.Core.Ipc;
using DlnRenderer.Core.Models;
using DlnRenderer.Services;
using Microsoft.UI.Dispatching;

namespace DlnRenderer.ViewModels;

/// <summary>
/// Projects the renderer service state onto bindable properties. The UI owns no player: playback,
/// DLNA discovery and video output all live in the service process, so closing the window does not
/// interrupt playback.
/// </summary>
/// <remarks>
/// Snapshots arrive on the pipe read thread and are marshalled through the captured dispatcher.
/// Commands are fire-and-forget through <see cref="ServiceClient"/>; the resulting state comes back
/// as a pushed snapshot, so the UI never guesses what happened.
/// </remarks>
public sealed class MainPageViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly ServiceClient _client = new();

    private string _serviceText = "正在连接后台服务…";
    private string _statusText = "待机";
    private string _detailText = "等待 DLNA 控制端推送媒体";
    private string _titleText = "未加载媒体";
    private string _positionText = "00:00";
    private string _durationText = "00:00";
    private double _positionSeconds;
    private double _durationSeconds;
    private double _volume = 100;
    private bool _hasError;
    private string _errorText = string.Empty;
    private bool _hasMedia;
    private bool _isPlaying;
    private bool _isConnected;

    public MainPageViewModel()
    {
        _client.SnapshotChanged += OnSnapshotChanged;
        _client.Faulted += OnClientFaulted;
    }

    public string ServiceText { get => _serviceText; private set => SetProperty(ref _serviceText, value); }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public string DetailText { get => _detailText; private set => SetProperty(ref _detailText, value); }

    public string TitleText { get => _titleText; private set => SetProperty(ref _titleText, value); }

    public string PositionText { get => _positionText; private set => SetProperty(ref _positionText, value); }

    public string DurationText { get => _durationText; private set => SetProperty(ref _durationText, value); }

    public double PositionSeconds { get => _positionSeconds; private set => SetProperty(ref _positionSeconds, value); }

    public double DurationSeconds { get => _durationSeconds; private set => SetProperty(ref _durationSeconds, value); }

    public double Volume { get => _volume; private set => SetProperty(ref _volume, value); }

    public bool HasError { get => _hasError; private set => SetProperty(ref _hasError, value); }

    public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }

    public bool HasMedia { get => _hasMedia; private set => SetProperty(ref _hasMedia, value); }

    public bool IsPlaying { get => _isPlaying; private set => SetProperty(ref _isPlaying, value); }

    public bool IsConnected { get => _isConnected; private set => SetProperty(ref _isConnected, value); }

    /// <summary>Connects to the service, starting it when it is not already running.</summary>
    public async Task InitializeAsync()
    {
        var connected = await _client.ConnectAsync(startIfMissing: true).ConfigureAwait(true);
        ApplyConnection(connected);
    }

    public Task PlayAsync() => SendAsync(IpcCommands.Play);

    public Task PauseAsync() => SendAsync(IpcCommands.Pause);

    public Task StopAsync() => SendAsync(IpcCommands.Stop);

    public Task SeekAsync(double seconds) => SendAsync(IpcCommands.Seek, seekSeconds: Math.Max(0, seconds));

    public Task SetVolumeAsync(double volume) => SendAsync(IpcCommands.SetVolume, volume: (int)Math.Round(volume));

    private async Task SendAsync(string command, double? seekSeconds = null, int? volume = null)
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

        var snapshot = await _client.SendAsync(command, seekSeconds, volume).ConfigureAwait(true);
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
        });
    }

    private void ApplyConnection(bool connected)
    {
        IsConnected = connected;
        ServiceText = connected ? "后台服务：已连接" : "后台服务：未连接";
    }

    private void ApplySnapshot(RendererSnapshot snapshot)
    {
        IsConnected = true;
        ServiceText = snapshot.DiscoveryActive
            ? $"后台服务：已连接 · 局域网发现已开启（{snapshot.DeviceName ?? "DlnRenderer"}）"
            : $"后台服务：已连接 · 发现未开启（{snapshot.DeviceName ?? "DlnRenderer"}）";

        var status = snapshot.Playback;
        HasMedia = status.Media is not null;
        IsPlaying = status.State == PlaybackState.Playing;

        StatusText = status.State switch
        {
            PlaybackState.Playing => "播放中",
            PlaybackState.Paused => "已暂停",
            PlaybackState.Buffering => "缓冲中",
            PlaybackState.Transitioning => "正在加载",
            PlaybackState.Error => "播放错误",
            PlaybackState.Stopped => HasMedia ? "已停止" : "待机",
            _ => "待机",
        };

        TitleText = status.Media?.Title
            ?? status.Media?.MediaUri.ToString()
            ?? "未加载媒体";

        DetailText = status.MediaKind switch
        {
            MediaKind.Video => "视频 · 画面显示在渲染器窗口中",
            MediaKind.Audio => "音频",
            _ => snapshot.DiscoveryActive ? "等待 DLNA 控制端推送媒体" : "等待 DLNA 控制端推送媒体",
        };

        PositionText = Format(status.Position);
        DurationText = status.Duration is { } duration ? Format(duration) : "00:00";
        PositionSeconds = status.Position.TotalSeconds;
        DurationSeconds = status.Duration?.TotalSeconds ?? 0;
        Volume = status.Volume;

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

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
