using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DlnRenderer.Core.Models;
using DlnRenderer.Player.Mpv;
using Microsoft.UI.Dispatching;

namespace DlnRenderer.ViewModels;

/// <summary>
/// Drives the minimal player UI. Owns the libmpv session and projects its status onto bindable
/// properties. Player commands run off the UI thread inside <see cref="MpvPlayer"/>; status
/// events arrive on mpv's thread and are marshalled back through the captured dispatcher.
/// </summary>
public sealed partial class MainPageViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    private MpvPlayer? _player;
    private MpvVideoSurface? _surface;
    private PlaybackStatus _status = PlaybackStatus.Empty;
    private bool _initialized;

    private string _statusText = "待机";
    private string _detailText = "播放器未初始化";
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

    public MediaKind MediaKind => _status.MediaKind;

    /// <summary>Creates the video surface and the mpv session. Must run on the UI thread.</summary>
    public void Initialize(nint parentWindowHandle)
    {
        if (_initialized)
        {
            return;
        }

        _surface = MpvVideoSurface.Create(parentWindowHandle);
        _player = new MpvPlayer(new MpvPlayerOptions
        {
            VideoWindowHandle = _surface.Handle,
            HardwareDecoding = true,
            PositionUpdateInterval = TimeSpan.FromMilliseconds(250),
        });

        _player.StatusChanged += OnStatusChanged;
        _player.LogMessage += (_, message) => DiagnosticsLog.Write($"mpv[{message.Level}] {message.Text}");

        DiagnosticsLog.Write($"surface created handle=0x{_surface.Handle.ToInt64():X}");
        _initialized = true;
        DetailText = "选择本地音频或视频文件开始播放";
        DiagnosticsLog.Write("player initialized");
    }

    public async Task OpenAsync(string path, string source = "ui")
    {
        if (_player is null)
        {
            return;
        }

        try
        {
            var uri = new Uri(Path.GetFullPath(path));
            var media = new MediaInfo(uri)
            {
                Title = Path.GetFileNameWithoutExtension(path),
            };

            HasError = false;
            ErrorText = string.Empty;
            DiagnosticsLog.Write($"loadfile {uri.LocalPath} source={source}");

            await _player.LoadAsync(media, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ReportError($"无法加载媒体: {ex.Message}");
        }
    }

    public async Task PlayAsync()
    {
        if (_player is null)
        {
            return;
        }

        try
        {
            await _player.PlayAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ReportError($"播放失败: {ex.Message}");
        }
    }

    public async Task PauseAsync()
    {
        if (_player is null)
        {
            return;
        }

        try
        {
            await _player.PauseAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ReportError($"暂停失败: {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        if (_player is null)
        {
            return;
        }

        try
        {
            await _player.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ReportError($"停止失败: {ex.Message}");
        }
    }

    public async Task SeekAsync(double seconds)
    {
        if (_player is null)
        {
            return;
        }

        try
        {
            await _player.SeekAsync(TimeSpan.FromSeconds(seconds), CancellationToken.None);
        }
        catch (Exception ex)
        {
            ReportError($"跳转失败: {ex.Message}");
        }
    }

    public async Task SetVolumeAsync(double volume)
    {
        if (_player is null)
        {
            return;
        }

        try
        {
            await _player.SetVolumeAsync((int)Math.Round(volume), CancellationToken.None);
            Volume = volume;
        }
        catch (Exception ex)
        {
            ReportError($"音量设置失败: {ex.Message}");
        }
    }

    /// <summary>Surfaces a player construction failure to the user instead of leaving a blank UI.</summary>
    public void ReportInitializationFailure(string message) => ReportError($"播放器初始化失败: {message}");

    /// <summary>Positions the native video window over the XAML placeholder. Coordinates are physical pixels.</summary>
    public void SetVideoBounds(int x, int y, int width, int height)
    {
        _surface?.SetBounds(x, y, width, height);
    }

    /// <summary>
    /// Tears down mpv and the native surface. Synchronous on purpose: it runs while the window is
    /// closing, and the surface must be destroyed before its parent window is gone.
    /// </summary>
    public void Shutdown()
    {
        _player?.StatusChanged -= OnStatusChanged;
        try
        {
            _player?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"player dispose failed: {ex}");
        }
        finally
        {
            _player = null;
            _surface?.Dispose();
            _surface = null;
            _initialized = false;
            DiagnosticsLog.Write("player disposed");
        }
    }

    private void OnStatusChanged(object? sender, PlaybackStatus status)
    {
        // Raised on mpv's event thread: never touch UI state or XAML from here.
        _dispatcher.TryEnqueue(() => ApplyStatus(status));
    }

    private void ApplyStatus(PlaybackStatus status)
    {
        _status = status;

        HasMedia = status.Media is not null;
        IsPlaying = status.State == PlaybackState.Playing;
        StatusText = status.State switch
        {
            PlaybackState.Stopped => "已停止",
            PlaybackState.Playing => "播放中",
            PlaybackState.Paused => "已暂停",
            PlaybackState.Buffering => "缓冲中",
            PlaybackState.Transitioning => "正在加载",
            PlaybackState.Error => "播放错误",
            _ => "待机",
        };

        TitleText = status.Media?.Title ?? status.Media?.MediaUri.ToString() ?? "未加载媒体";
        DetailText = status.MediaKind switch
        {
            MediaKind.Video => "视频",
            MediaKind.Audio => "音频",
            _ => "选择本地音频或视频文件开始播放",
        };

        PositionText = Format(status.Position);
        DurationText = status.Duration is { } d ? Format(d) : "00:00";
        PositionSeconds = status.Position.TotalSeconds;
        DurationSeconds = status.Duration?.TotalSeconds ?? 0;
        Volume = status.Volume;

        HasError = status.State == PlaybackState.Error;
        ErrorText = status.ErrorMessage ?? string.Empty;

        // The native child window is only shown when the file really contains video.
        var showVideo = status.MediaKind == MediaKind.Video;
        _surface?.SetVisible(showVideo);
        DiagnosticsLog.Write($"video surface visible={showVideo} surfaceNull={_surface is null}");

        DiagnosticsLog.Write(
            $"status state={status.State} kind={status.MediaKind} pos={status.Position} dur={status.Duration} vol={status.Volume}");
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
}
