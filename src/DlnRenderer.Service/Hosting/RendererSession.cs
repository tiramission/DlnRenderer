using DlnRenderer.Core.Ipc;
using DlnRenderer.Core.Models;
using DlnRenderer.Dlna;
using DlnRenderer.Dlna.AvTransport;
using DlnRenderer.Dlna.Metadata;
using DlnRenderer.Player.Mpv;

namespace DlnRenderer.Service.Hosting;

/// <summary>
/// Bridges the DLNA protocol layer, the IPC layer and the real libmpv player. This is the only
/// place that knows all three, and it is the single writer of renderer state.
/// </summary>
public sealed class RendererSession : ITransportRenderer, IRendererControl, IAsyncDisposable
{
    private readonly MpvPlayer _player;
    private readonly VideoPresenter? _presenter;
    private readonly DlnaOptions _options;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _stateGate = new();

    private Uri? _uri;
    private string? _metadata;
    private string? _title;
    private PlaybackStatus _status = PlaybackStatus.Empty;
    private string? _lastError;
    private bool _disposed;

    public RendererSession(MpvPlayer player, VideoPresenter? presenter, DlnaOptions options, Action<string> log)
    {
        _player = player;
        _presenter = presenter;
        _options = options;
        _log = log;
        _player.StatusChanged += OnPlayerStatusChanged;
    }

    /// <summary>Raised on the player's event thread whenever the published snapshot changes.</summary>
    public event Action<RendererSnapshot>? SnapshotChanged;

    public Uri? CurrentUri
    {
        get
        {
            lock (_stateGate)
            {
                return _uri;
            }
        }
    }

    public string? CurrentMetadata
    {
        get
        {
            lock (_stateGate)
            {
                return _metadata;
            }
        }
    }

    public bool DiscoveryActive { get; set; }

    public string? ControlUrl { get; set; }

    public RendererSnapshot GetSnapshot()
    {
        PlaybackStatus status;
        string? error;
        lock (_stateGate)
        {
            status = _status;
            error = _lastError;
        }

        return new RendererSnapshot
        {
            Playback = status,
            DeviceName = _options.FriendlyName,
            DiscoveryActive = DiscoveryActive,
            ControlUrl = ControlUrl,
            LastError = error,
        };
    }

    public async Task LoadAsync(Uri uri, string? metadataXml, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var parsed = DidlLiteParser.Parse(metadataXml);
        var media = new MediaInfo(uri)
        {
            Title = parsed.Title ?? Path.GetFileName(uri.LocalPath),
            Artist = parsed.Artist,
            Album = parsed.Album,
            MimeType = parsed.MimeType,
            Duration = parsed.Duration,
            CoverUri = Uri.TryCreate(parsed.AlbumArtUri, UriKind.Absolute, out var cover) ? cover : null,
        };

        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                // Store the intent before touching the player so status queries never describe
                // the previous media while the new load is in flight.
                _uri = uri;
                _metadata = metadataXml;
                _title = media.Title;
                _lastError = null;
            }

            PlayerVisibility(show: false);

            try
            {
                await _player.LoadAsync(media, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (_stateGate)
                {
                    _uri = null;
                    _metadata = null;
                    _title = null;
                }

                throw;
            }

            _log($"player loading {Describe(uri)}");
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task PlayAsync(CancellationToken cancellationToken)
    {
        RequireMedia("Play");
        await ExecuteAsync("play", async () =>
        {
            var status = await _player.GetStatusAsync(cancellationToken).ConfigureAwait(false);

            // After Stop (or a failed load) the URI is still the current one but the player has no
            // media: mpv would "play" nothing. Reload first so Play means play.
            if (_uri is not null
                && (status.State == PlaybackState.Error || status.Media is null || status.State == PlaybackState.Stopped))
            {
                _log("reloading media before play");
                await ReloadCurrentAsync(cancellationToken).ConfigureAwait(false);
            }

            await _player.PlayAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        RequireMedia("Pause");
        return ExecuteAsync("pause", () => _player.PauseAsync(cancellationToken), cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync("stop", async () =>
        {
            await _player.StopAsync(cancellationToken).ConfigureAwait(false);
            PlayerVisibility(show: false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        // Seeking without media would silently do nothing on the player side.
        RequireMedia("Seek");
        return ExecuteAsync("seek", async () =>
        {
            var status = await _player.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.Media is null || status.State == PlaybackState.Stopped)
            {
                _log("reloading media before seek");
                await ReloadCurrentAsync(cancellationToken).ConfigureAwait(false);
            }

            await _player.SeekAsync(position, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>
    /// Rejects transport commands that need loaded media. The player's own commands are no-ops
    /// when idle, so without this an unloaded renderer would report success for Play/Pause/Seek.
    /// </summary>
    private void RequireMedia(string operation)
    {
        var hasMedia = _uri is not null;
        if (!hasMedia)
        {
            throw new InvalidOperationException($"No media has been set; {operation} is not available.");
        }
    }

    public Task SetVolumeAsync(int volume, CancellationToken cancellationToken)
        => ExecuteAsync("volume", () => _player.SetVolumeAsync(volume, cancellationToken), cancellationToken);

    public Task SetMuteAsync(bool muted, CancellationToken cancellationToken)
        => ExecuteAsync("mute", () => _player.SetMuteAsync(muted, cancellationToken), cancellationToken);

    /// <summary>Player volume (0-100) read from the player, not the Windows mixer.</summary>
    public async Task<int> GetVolumeAsync(CancellationToken cancellationToken)
    {
        var status = await _player.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return status.Volume;
    }

    public async Task<bool> GetMuteAsync(CancellationToken cancellationToken)
    {
        var status = await _player.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return status.Muted;
    }

    public async Task<TransportSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var status = await _player.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        Uri? uri;
        string? title;
        lock (_stateGate)
        {
            uri = _uri;
            title = _title;
        }

        // A snapshot describing a different URI is a stale event from the media we replaced.
        if (uri is not null
            && status.State != PlaybackState.Error
            && status.Media is not null
            && status.Media.MediaUri != uri)
        {
            return new TransportSnapshot(PlaybackState.Transitioning, TimeSpan.Zero, null, title, null);
        }

        return new TransportSnapshot(
            status.State,
            status.Position,
            status.Duration,
            status.Media?.Title ?? title,
            status.ErrorMessage);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _player.StatusChanged -= OnPlayerStatusChanged;

        await _player.DisposeAsync().ConfigureAwait(false);
        _commandGate.Dispose();
    }

    private async Task ReloadCurrentAsync(CancellationToken cancellationToken)
    {
        Uri? uri;
        string? metadata;
        lock (_stateGate)
        {
            uri = _uri;
            metadata = _metadata;
        }

        if (uri is null)
        {
            return;
        }

        var parsed = DidlLiteParser.Parse(metadata);
        var media = new MediaInfo(uri)
        {
            Title = parsed.Title ?? _title,
            MimeType = parsed.MimeType,
            Duration = parsed.Duration,
            Artist = parsed.Artist,
            Album = parsed.Album,
        };

        await _player.LoadAsync(media, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(
        string label,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"{label} failed: {ex.Message}");
            throw;
        }
        finally
        {
            _commandGate.Release();
        }

        _log($"player command {label} applied");
    }

    private void OnPlayerStatusChanged(object? sender, PlaybackStatus status)
    {
        Uri? uri;
        lock (_stateGate)
        {
            uri = _uri;

            // Ignore events raised for media that has already been replaced.
            if (uri is not null && status.Media is not null && status.Media.MediaUri != uri)
            {
                return;
            }

            _status = status;
            _lastError = status.ErrorMessage;
        }

        if (status.State is PlaybackState.Playing or PlaybackState.Paused && status.MediaKind == MediaKind.Video)
        {
            PlayerVisibility(show: true);
        }
        else if (status.State is PlaybackState.Stopped or PlaybackState.Error)
        {
            PlayerVisibility(show: false);
        }

        SnapshotChanged?.Invoke(GetSnapshot());
    }

    private void PlayerVisibility(bool show)
    {
        if (_presenter is null)
        {
            _log("video presenter unavailable; video will not be shown");
            return;
        }

        if (show)
        {
            if (!_presenter.IsVisible)
            {
                _presenter.Show();
                _log("video presenter shown");
            }
        }
        else
        {
            _presenter.Hide();
        }
    }

    private static string Describe(Uri uri)
        => uri.UserInfo.Length > 0
            ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.SafeUnescaped)
            : uri.ToString();
}
