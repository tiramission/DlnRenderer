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
    private bool _lastKnownVideo;
    private bool _playbackStarted;
    private bool _stopBarrier;
    private bool _needsReload;
    private bool _disposed;

    /// <summary>
    /// Position a control point asked for while no medium was loaded, to be applied once one is.
    /// </summary>
    /// <remarks>
    /// Seeking is a progress-bar drag: the client expects the position to move, not to wait. When the
    /// medium has ended or been discarded, honouring the seek needs a reload, which can take as long
    /// as the network does. Blocking the request on that made the slider appear dead, so the target
    /// is remembered here and applied by the reload that follows - from this renderer's background
    /// task, or from the next Play.
    /// </remarks>
    private TimeSpan? _pendingSeek;

    /// <summary>1 while a background reload-and-seek is in flight; guards against a pile-up.</summary>
    private int _backgroundSeekRunning;

    /// <summary>Where video may be presented; replaced by the UI over IPC.</summary>
    private VideoTarget _videoTarget = VideoTarget.Default;

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

    /// <summary>
    /// Raised when AVTransport state that GENA subscribers care about changed (transport state,
    /// status, duration) or when the current medium was replaced.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SnapshotChanged"/> because the IPC snapshot also changes on every
    /// position update, while eventing must stay proportional to real state changes.
    /// </remarks>
    public event Action<PlaybackStatus>? TransportEventRaised;

    /// <summary>Raised when volume or mute changed. Carries the values the player actually reports.</summary>
    public event Action<int, bool>? RenderingEventRaised;

    /// <summary>Raised when a new medium became the current one.</summary>
    public event Action? MediaReplaced;

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

    /// <summary>
    /// Where video may be shown, as last reported by the UI. Also drives the presentation the next
    /// time a status change asks for the picture to come up.
    /// </summary>
    public VideoTarget VideoTarget
    {
        get
        {
            lock (_stateGate)
            {
                return _videoTarget;
            }
        }
    }

    /// <summary>
    /// Applies a new presentation target and re-evaluates visibility immediately.
    /// </summary>
    /// <remarks>
    /// Re-evaluating matters in both directions: switching to embedded while a video is playing must
    /// move the picture into the window at once, and switching back to fullscreen must not wait for
    /// the next player event.
    /// </remarks>
    public void SetVideoTarget(VideoTarget? target)
    {
        var effective = target ?? VideoTarget.Default;

        PlaybackStatus status;
        bool requested;
        bool playbackStarted;
        bool lastKnownVideo;
        lock (_stateGate)
        {
            _videoTarget = effective;
            status = _status;
            requested = _uri is not null;
            playbackStarted = _playbackStarted;
            lastKnownVideo = _lastKnownVideo;
        }

        _presenter?.SetTarget(effective);

        PlayerVisibility(
            VideoWindowPolicy.Decide(status, requested, lastKnownVideo, playbackStarted)
                == VideoWindowMode.Shown);
    }

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

        // Hiding before the new file is opened avoids showing the tail of the previous video (or a
        // black frame from the freshly reset surface) while the new one buffers.
        PlayerVisibility(show: false);

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
            // Store the intent before touching the player so status queries never describe the
            // previous media while the new load is in flight.
            lock (_stateGate)
            {
                _uri = uri;
                _metadata = metadataXml;
                _title = media.Title;
                _lastError = null;

                // A new medium starts from a clean slate: nothing has been presented yet, and
                // libmpv opens every file paused, which must not take the screen.
                _lastKnownVideo = false;
                _playbackStarted = false;
                _stopBarrier = false;
                _needsReload = false;

                // A position remembered for the previous item means nothing for this one.
                _pendingSeek = null;
            }

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

            // The current URI is part of every AVTransport event, so a media switch is an event even
            // when the transport state happens to stay the same (A -> B while playing).
            MediaReplaced?.Invoke();
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

            // Reload when the player has no media left, or when Stop ended the previous run: mpv then
            // still reports the old title but has nothing to play, and a bare Play would show an empty
            // window. The reload is awaited so this call does not return before playback can start.
            if (status.Media is null
                || status.State is PlaybackState.Stopped or PlaybackState.Error
                || _needsReload)
            {
                _log("reloading media before play");
                await ReloadCurrentAsync(cancellationToken).ConfigureAwait(false);

                var reloaded = await _player.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (reloaded.Media is null || reloaded.State == PlaybackState.Error)
                {
                    throw new InvalidOperationException(
                        $"Media could not be loaded: {reloaded.ErrorMessage ?? "the player has no media"}");
                }
            }

            // Announce the intent before issuing Play: the player reports Playing from its own thread,
            // and a Paused status must already read as "the user paused" when that event lands.
            lock (_stateGate)
            {
                _playbackStarted = true;
                _stopBarrier = false;
                _needsReload = false;
            }

            // A seek requested while the medium was unloaded takes effect here, before playback
            // starts, so the first frame is the one the control point asked for.
            await ApplyPendingSeekCoreAsync(cancellationToken).ConfigureAwait(false);

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

            lock (_stateGate)
            {
                // Stop is authoritative from here on: events already queued by mpv still describe the
                // media that was playing and arrive as "Playing", and acting on them would put the
                // overlay back up after the cast ended.
                _playbackStarted = false;
                _stopBarrier = true;

                // The next Play must reload: the player keeps the old title but cannot play it again.
                _needsReload = true;
            }

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

            // Fast path: the medium is loaded, so the player can move within it right now.
            if (status.Media is not null && status.State is not (PlaybackState.Stopped or PlaybackState.Error))
            {
                try
                {
                    await _player.SeekAsync(position, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (MpvCommandException ex)
                {
                    // Stop can land between the status read above and this command, which leaves the
                    // player idle and unwilling to seek. Fall through to the deferred path rather
                    // than surfacing a failure the control point cannot act on.
                    _log($"seek refused by the player ({ex.Message}); deferring");
                }
            }

            // The medium ended, was discarded, or the player refused the seek. Recording the target
            // and reloading in the background keeps the request instantaneous: a control point that
            // waits ~15 s for a reload gives up on the progress bar, and every drag step would queue
            // another one.
            lock (_stateGate)
            {
                _pendingSeek = position;
            }

            _log($"seek deferred until the media is reloaded: {position.TotalSeconds:F1}s");
            KickBackgroundSeek();
        }, cancellationToken);
    }

    /// <summary>
    /// Applies a seek that was deferred because no medium was loaded.
    /// </summary>
    /// <remarks>
    /// The caller must already hold the command gate. The target is cleared only after the player
    /// accepted it, so a reload that times out leaves it in place for the next Play to apply.
    /// </remarks>
    private async Task ApplyPendingSeekCoreAsync(CancellationToken cancellationToken)
    {
        TimeSpan? target;
        lock (_stateGate)
        {
            target = _pendingSeek;
        }

        if (target is null)
        {
            return;
        }

        var status = await _player.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.Media is null || status.State is PlaybackState.Stopped or PlaybackState.Error)
        {
            _log("reloading media to honour a seek");
            await ReloadCurrentAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_stateGate)
        {
            // Another seek may have landed while the reload ran; the newest target wins.
            target = _pendingSeek;
            _pendingSeek = null;
        }

        if (target is null)
        {
            return;
        }

        await _player.SeekAsync(target.Value, cancellationToken).ConfigureAwait(false);
        _log($"seek applied after reload: {target.Value.TotalSeconds:F1}s");
    }

    /// <summary>
    /// Runs the deferred seek on the thread pool, at most once at a time.
    /// </summary>
    /// <remarks>
    /// Nothing awaits this: the control point was already answered. A second seek while the first
    /// reload is still running only updates the target - the running task applies the newest value.
    /// </remarks>
    private void KickBackgroundSeek()
    {
        if (Interlocked.CompareExchange(ref _backgroundSeekRunning, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // A network load that never completes must not pin this task for the process lifetime.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _commandGate.WaitAsync(timeout.Token).ConfigureAwait(false);
                try
                {
                    await ApplyPendingSeekCoreAsync(timeout.Token).ConfigureAwait(false);
                }
                finally
                {
                    _commandGate.Release();
                }
            }
            catch (Exception ex)
            {
                // The target stays pending: the next Play reloads and applies it.
                _log($"background seek abandoned: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _backgroundSeekRunning, 0);
            }
        });
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

        // Take the overlay down before the player goes away, otherwise a destroyed mpv surface could
        // leave a fullscreen window painting a blank frame during shutdown.
        PlayerVisibility(show: false);

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

        // Reloading right after Stop overlaps libmpv's own teardown of the previous file: the load can
        // be discarded without any event, leaving the status at Transitioning forever. Retry a couple
        // of times with a bounded wait, then fail loudly - a caller must never be told Play succeeded
        // when nothing is going to play.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await _player.LoadAsync(media, cancellationToken).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var status = await _player.GetStatusAsync(cancellationToken).ConfigureAwait(false);

                // Buffering counts as settled: the file is open and the player has reported it, which
                // is all this wait exists to establish. A stream that is still filling its cache must
                // not be mistaken for a failed load, and a Play immediately after will start it.
                if (status.State is PlaybackState.Paused or PlaybackState.Playing
                    or PlaybackState.Buffering or PlaybackState.Error)
                {
                    return;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            _log($"reload attempt {attempt} did not settle; retrying");
        }

        throw new TimeoutException("Media did not finish loading after 3 attempts.");
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
        bool requested;
        bool playbackStarted;
        lock (_stateGate)
        {
            var uri = _uri;

            // Ignore events raised for media that has already been replaced.
            if (uri is not null && status.Media is not null && status.Media.MediaUri != uri)
            {
                return;
            }

            // After Stop, queued events describing the stopped medium arrive as Playing and must not
            // restart the presentation. Only Play is filtered, for exactly that reason: a reload
            // issues Transitioning, Buffering, Paused and Error, and swallowing those leaves every
            // caller waiting on the reload observing a state that never advances.
            if (_stopBarrier
                && !_playbackStarted
                && status.State is PlaybackState.Playing)
            {
                return;
            }

            requested = uri is not null;
            playbackStarted = _playbackStarted;

            _status = status;
            _lastError = status.ErrorMessage;

            // Remember the kind: mpv reports Unknown again once the file ends or is replaced, right
            // before the next Play reloads it.
            if (status.MediaKind != MediaKind.Unknown)
            {
                _lastKnownVideo = status.MediaKind == MediaKind.Video;
            }
        }

        // One decision, from VideoWindowPolicy: show from the first video frame until Stop/Error,
        // and treat the event as stale when it describes media the player is no longer serving.
        PlayerVisibility(
            VideoWindowPolicy.Decide(status, requested, _lastKnownVideo, playbackStarted)
                == VideoWindowMode.Shown);

        _log($"player status {status.State} kind={status.MediaKind} pos={status.Position.TotalSeconds:F1} error={status.ErrorMessage}");
        SnapshotChanged?.Invoke(GetSnapshot());

        // Eventing is fed from the same status the IPC snapshot and the overlay use, so a subscriber
        // can never be told something the renderer itself does not believe.
        TransportEventRaised?.Invoke(status);
        RenderingEventRaised?.Invoke(status.Volume, status.Muted);
    }

    /// <summary>
    /// Applies the presentation window state, in one place, from every caller that can change it.
    /// </summary>
    /// <remarks>
    /// Player events arrive on mpv's thread, DLNA commands on request threads and IPC commands on the
    /// pipe thread. Centralising the decision here is what keeps a slow Stop from hiding a window a
    /// fast Play has already claimed.
    /// <para>
    /// There is deliberately no "already applied" short circuit: the presenter itself makes
    /// <see cref="VideoPresenter.ShowFullscreen"/>/<see cref="VideoPresenter.Hide"/> idempotent, and
    /// remembering the last requested mode went stale as soon as another path (a media load, a
    /// display change) touched the window, leaving the overlay up over a paused, not-yet-played
    /// stream.
    /// </para>
    /// </remarks>
    private void PlayerVisibility(bool show)
    {
        if (_presenter is null)
        {
            _log("video presenter unavailable; video will not be shown");
            return;
        }

        if (show)
        {
            // Mode-aware: the presenter embeds into the UI window when the target says so, and
            // falls back to fullscreen when it cannot.
            _presenter.Show();
            _log($"video presenter {_presenter.PresentationState} on monitor 0x{_presenter.MonitorHandle:X}");
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
