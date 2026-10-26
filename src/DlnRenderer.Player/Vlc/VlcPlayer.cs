using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DlnRenderer.Core.Interfaces;
using DlnRenderer.Core.Models;
using DlnRenderer.Core.State;
using LibVLCSharp.Shared;

namespace DlnRenderer.Player.Vlc;

/// <summary>
/// libvlc-backed implementation of <see cref="IMediaPlayer"/>. All native calls live behind this
/// type: callers never touch libvlc directly.
/// </summary>
/// <remarks>
/// <para>
/// Threading: libvlc raises its events on its own threads. <see cref="StatusChanged"/> is raised
/// from those threads, so UI consumers must marshal to their dispatcher. Commands are invoked on
/// the thread pool; the renderer session serialises them, so no internal command ordering is needed.
/// </para>
/// <para>
/// Loading semantics follow <c>SetAVTransportURI</c>: a load must not start playback. libvlc has no
/// "open paused" mode, so the adapter issues <c>Play</c> and immediately <c>Pause</c> - the input is
/// created paused (the request is honoured while the medium is still opening), and a guard on the
/// <c>Playing</c> event re-applies the pause should the two calls race. The renderer session accepts
/// Paused/Buffering as "the load has settled", which is exactly the state this produces.
/// </para>
/// <para>
/// Unlike mpv, libvlc does not tag events with the media they belong to. Stale events are filtered
/// with a load-pending flag (state-ending events are swallowed while a load is in flight, mirroring
/// the mpv adapter) and, as a second net, the session drops events whose <see cref="PlaybackStatus.Media"/>
/// no longer matches the current one.
/// </para>
/// </remarks>
public sealed class VlcPlayer : IMediaPlayer
{
    private readonly VlcPlayerOptions _options;
    private readonly PlaybackStateMachine _stateMachine = new();
    private readonly PositionThrottle _throttle;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();

    private LibVLC? _libVlc;
    private MediaPlayer? _player;

    // Signals mirrored from libvlc, guarded by _gate.
    private bool _loadPending;
    private bool _desiredPause = true;
    private bool _hasVideo;
    private bool _hasAudio;
    private bool _reachedEnd;
    private string? _error;
    private double _position;
    private double _duration;
    private int _volume = 100;
    private bool _muted;

    private MediaInfo? _media;
    private PlaybackStatus _lastPublished = PlaybackStatus.Empty;
    private bool _disposed;

    public VlcPlayer(VlcPlayerOptions? options = null)
    {
        _options = options ?? new VlcPlayerOptions();
        _throttle = new PositionThrottle(_options.PositionUpdateInterval);

        try
        {
            // Locates libvlc\win-<arch>\ next to the executable and adds it to the DLL search path.
            // Without this, "libvlc.dll" resolves against PATH and fails on a normal install. Fully
            // qualified: "Core" also names our own DlnRenderer.Core namespace.
            LibVLCSharp.Shared.Core.Initialize();
        }
        catch (Exception ex)
        {
            throw new VlcInitializationException(
                "Could not locate the libvlc native libraries. Expected them under " +
                $"'{Path.Combine(AppContext.BaseDirectory, "libvlc")}' next to the executable. " +
                "The VideoLAN.LibVLC.Windows package must have been restored into the service output.",
                ex);
        }

        try
        {
            _libVlc = CreateLibVlc();
            _player = new MediaPlayer(_libVlc)
            {
                Hwnd = _options.VideoWindowHandle,

                // VLC's vout installs its own mouse handling over the embedded picture: an
                // aggressive cursor auto-hide (a blank cursor after ~1 s idle) and, while it
                // manages the pointer, janky show/hide transitions that read as a busy cursor
                // inside the application window. The renderer owns every interaction through the
                // UI, so VLC gets neither mouse nor keyboard.
                EnableMouseInput = false,
                EnableKeyInput = false,
            };

            AttachEventHandlers(_player);
            _libVlc.Log += OnLibVlcLog;
        }
        catch (Exception ex) when (ex is not VlcInitializationException)
        {
            _ = Task.Run(DisposeNative);
            throw new VlcInitializationException($"libvlc could not be initialised: {ex.Message}", ex);
        }
    }

    public event EventHandler<PlaybackStatus>? StatusChanged;

    /// <summary>Forwarded libvlc log lines at or above the configured verbosity.</summary>
    public event EventHandler<VlcLogMessage>? LogMessage;

    public async Task LoadAsync(MediaInfo media, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (!media.MediaUri.IsAbsoluteUri)
        {
            throw new ArgumentException("Media URI must be absolute.", nameof(media));
        }

        var player = RequirePlayer();
        var libVlc = RequireLibVlc();

        // The Uri overload picks the right libvlc constructor per scheme: local paths go through
        // libvlc_media_new_path, remote URIs through libvlc_media_new_location. The string overload
        // defaults to path semantics and would try to open an http:// URL as a file on disk - which
        // is how every real cast failed while local-file smoke tests passed.
        var nativeMedia = new Media(libVlc, media.MediaUri);

        lock (_gate)
        {
            _media = media;
            _error = null;
            _position = 0;
            _duration = 0;
            _reachedEnd = false;
            _hasVideo = false;
            _hasAudio = false;
            _loadPending = true;
            _desiredPause = true;
        }

        // Announce the load before touching the player, the same way the mpv adapter does: callers
        // waiting on the Transitioning edge must be able to observe it even when the medium loads fast.
        _stateMachine.TryTransitionTo(PlaybackState.Stopped);
        _stateMachine.TryTransitionTo(PlaybackState.Transitioning);

        try
        {
            await Task.Run(
                () =>
                {
                    ThrowIfDisposed();

                    // A medium may still be playing: setting a new Media does not stop the old input
                    // in libvlc 3, and two live inputs would fight over the audio device.
                    if (player.State is VLCState.Playing or VLCState.Paused or VLCState.Buffering or VLCState.Opening)
                    {
                        player.Stop();
                    }

                    player.Media = nativeMedia;

                    // "Open paused": Play creates the input, Pause ensures it starts paused. The
                    // Playing-event guard re-applies the pause if the pair raced.
                    player.Play();
                    player.Pause();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (VLCException ex)
        {
            lock (_gate)
            {
                _loadPending = false;
                _media = null;
            }

            Publish(urgent: true);
            throw new PlayerCommandException($"vlc could not load {media.MediaUri}: {ex.Message}", ex);
        }
        catch
        {
            lock (_gate)
            {
                _loadPending = false;
            }

            Publish(urgent: true);
            throw;
        }
    }

    public Task PlayAsync(CancellationToken cancellationToken)
    {
        var player = RequirePlayer();
        lock (_gate)
        {
            // A Play racing the load must win, exactly like the mpv adapter's pause intent.
            _desiredPause = false;
        }

        return RunCommandAsync(cancellationToken, () =>
        {
            if (player.State is VLCState.Stopped or VLCState.Ended or VLCState.NothingSpecial)
            {
                // After a stop or end, the media object is detached from the input; a bare Play would
                // do nothing. Re-attaching the remembered medium makes Play behave like the control
                // point expects, and the session reloads first for anything more complex.
                var media = _media;
                var libVlc = RequireLibVlc();
                if (media is not null && player.Media is null)
                {
                    player.Media = new Media(libVlc, media.MediaUri.IsFile
                        ? media.MediaUri.LocalPath
                        : media.MediaUri.AbsoluteUri);
                }
            }

            player.Play();
        });
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        var player = RequirePlayer();
        lock (_gate)
        {
            _desiredPause = true;
        }

        return RunCommandAsync(cancellationToken, player.Pause);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var player = RequirePlayer();
        lock (_gate)
        {
            // An explicit stop wins over an in-flight load; state-ending events for the replaced
            // medium are stale from here on and the load's own events take over again.
            _loadPending = false;
            _desiredPause = true;
        }

        await RunCommandAsync(cancellationToken, () =>
        {
            if (player.State is not (VLCState.NothingSpecial or VLCState.Stopped or VLCState.Ended))
            {
                player.Stop();
            }
        }).ConfigureAwait(false);

        // An input that already ended (or never opened) fires no Stopped event when Stop is
        // requested, yet the session and every control point need Stop to land in Stopped. The
        // publish below maps the ended state to Stopped; resetting the position and track kinds
        // here is what a user-initiated stop means.
        lock (_gate)
        {
            _reachedEnd = false;
            _position = 0;
            _hasVideo = false;
            _hasAudio = false;
        }

        Publish(urgent: true);
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (position < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Seek position cannot be negative.");
        }

        var player = RequirePlayer();

        // libvlc's set-time is a silent no-op without a live input, which the renderer session would
        // read as success. The seek contract requires an honest refusal instead.
        var state = player.State;
        if (state is not (VLCState.Playing or VLCState.Paused or VLCState.Buffering))
        {
            throw new VlcCommandException("seek", $"the player is {state} and has no seekable input");
        }

        await RunCommandAsync(cancellationToken, () =>
        {
            try
            {
                player.Time = (long)position.TotalMilliseconds;
            }
            catch (VLCException ex)
            {
                throw new VlcCommandException("seek", ex.Message);
            }
        }).ConfigureAwait(false);

        // libvlc applies the move but does not raise TimeChanged while paused, so the cache would
        // keep the pre-seek position until playback resumed - a control point reading the position
        // right after a paused seek would see the old value. The same "after the call, reads see
        // the value" rule as SetVolumeAsync applies here.
        lock (_gate)
        {
            _position = position.TotalSeconds;
        }

        Publish(urgent: true);
    }

    public async Task SetVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        if (volume is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be between 0 and 100.");
        }

        var player = RequirePlayer();
        await RunCommandAsync(cancellationToken, () =>
        {
            try
            {
                // libvlc stores the value on the player even with no audio output live, so a volume
                // change while idle still takes effect for the next medium; a VLCException here
                // means a live output actually refused, which must surface.
                player.Volume = volume;
            }
            catch (VLCException ex)
            {
                throw new VlcCommandException("set volume", ex.Message);
            }
        }).ConfigureAwait(false);

        lock (_gate)
        {
            _volume = volume;
        }

        Publish(urgent: true);
    }

    public async Task SetMuteAsync(bool muted, CancellationToken cancellationToken)
    {
        var player = RequirePlayer();
        await RunCommandAsync(cancellationToken, () =>
        {
            try
            {
                player.Mute = muted;
            }
            catch (VLCException ex)
            {
                throw new VlcCommandException("set mute", ex.Message);
            }
        }).ConfigureAwait(false);

        lock (_gate)
        {
            _muted = muted;
        }

        Publish(urgent: true);
    }

    public Task<PlaybackStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return Task.FromResult(BuildStatus());
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Task.Run(DisposeNative).ConfigureAwait(false);
    }

    /// <summary>
    /// Tears the native objects down. Runs on the thread pool because libvlc's Stop and Dispose can
    /// block while an input is closing, and callers dispose from whatever thread owns the session.
    /// </summary>
    private void DisposeNative()
    {
        var libVlc = Interlocked.Exchange(ref _libVlc, null);
        var player = Interlocked.Exchange(ref _player, null);

        if (player is not null)
        {
            DetachEventHandlers(player);
            try
            {
                if (player.State is VLCState.Playing or VLCState.Paused or VLCState.Buffering or VLCState.Opening)
                {
                    player.Stop();
                }
            }
            catch
            {
                // A refusal to stop must not prevent the native handles from being released.
            }

            player.Dispose();
        }

        if (libVlc is not null)
        {
            libVlc.Log -= OnLibVlcLog;
            libVlc.Dispose();
        }
    }

    private LibVLC CreateLibVlc()
    {
        // --no-video-title-show: the renderer draws its own title; libvlc's filename overlay would
        // flash over the picture on every cast.
        // --no-osd keeps libvlc from painting its own feedback (position popups) into the picture.
        var options = new List<string> { "--no-video-title-show", "--no-osd" };

        if (_options.VideoWindowHandle != 0)
        {
            // --vout=direct3d9: VLC 3's D3D11 output presents with the flip model into its own
            // child-window stack, which is the known source of embedded-rendering artifacts
            // (stale rectangles when the host window moves, unpainted letterbox areas reading as
            // green garbage, green corruption from D3D11VA surfaces). The D3D9 output presents
            // with a plain blit, clears every region it owns and is the embedding path VLC itself
            // shipped for years. Quality-critical playback belongs to the mpv engine; this engine
            // is the compatibility fallback, and correctness wins over peak quality here.
            options.Add("--vout=direct3d9");

            // dxva2 pairs with the D3D9 output: with the default "any", the D3D11VA decoder wins
            // the probe and its surfaces cannot feed a D3D9 swapchain, so every cast pays a
            // decoder-negotiation detour first.
            options.Add(_options.HardwareDecoding ? "--avcodec-hw=dxva2" : "--avcodec-hw=none");
        }
        else if (!_options.HardwareDecoding)
        {
            options.Add("--avcodec-hw=none");
        }

        return new LibVLC(options.ToArray());
    }

    private void AttachEventHandlers(MediaPlayer player)
    {
        player.Playing += OnPlaying;
        player.Paused += OnPaused;
        player.Stopped += OnStopped;
        player.EndReached += OnEndReached;
        player.EncounteredError += OnEncounteredError;
        player.Opening += OnOpening;
        player.Buffering += OnBuffering;
        player.TimeChanged += OnTimeChanged;
        player.LengthChanged += OnLengthChanged;
        player.VolumeChanged += OnVolumeChanged;
        player.Muted += OnMuted;
        player.Unmuted += OnUnmuted;
    }

    private void DetachEventHandlers(MediaPlayer player)
    {
        player.Playing -= OnPlaying;
        player.Paused -= OnPaused;
        player.Stopped -= OnStopped;
        player.EndReached -= OnEndReached;
        player.EncounteredError -= OnEncounteredError;
        player.Opening -= OnOpening;
        player.Buffering -= OnBuffering;
        player.TimeChanged -= OnTimeChanged;
        player.LengthChanged -= OnLengthChanged;
        player.VolumeChanged -= OnVolumeChanged;
        player.Muted -= OnMuted;
        player.Unmuted -= OnUnmuted;
    }

    // ---- event handlers (libvlc threads) ------------------------------------------------------

    private void OnOpening(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _reachedEnd = false;
        }

        Publish(urgent: true);
    }

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        bool buffering = e.Cache < 99.5f;
        if (!buffering)
        {
            // The cache-full event is the normal end of opening, not a state of its own.
            return;
        }

        Publish(urgent: true);
    }

    private void OnPlaying(object? sender, EventArgs e)
    {
        bool reapplyPause;
        lock (_gate)
        {
            _loadPending = false;
            _error = null;
            _reachedEnd = false;
            reapplyPause = _desiredPause;
        }

        // The vout windows exist from this event on (re-created for each new vout session), so the
        // cursor guard re-runs here; it subclasses only windows it has not seen before.
        VlcCursorGuard.Install(_options.VideoWindowHandle);

        if (reapplyPause)
        {
            try
            {
                RequirePlayer().Pause();
            }
            catch
            {
                // The player was disposed between the event and this call; the disposal path owns
                // reporting that. Nothing here can recover a disposed player anyway.
            }
        }

        RefreshTrackPresence();
        Publish(urgent: true);
    }

    private void OnPaused(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _loadPending = false;
        }

        RefreshTrackPresence();
        Publish(urgent: true);
    }

    private void OnStopped(object? sender, EventArgs e)
    {
        // Stop() of the replaced input during a load lands here; swallowing it keeps the load's
        // Transitioning state intact until the new medium reports for itself.
        bool stale;
        lock (_gate)
        {
            stale = _loadPending;
        }

        if (stale)
        {
            return;
        }

        bool ended;
        lock (_gate)
        {
            // libvlc releases the input a moment after a natural end, which surfaces as a Stopped
            // event on top of EndReached. Zeroing the position here would rewind a medium the
            // control point just watched to completion - the end-of-media position is part of the
            // AVTransport contract, exactly as for mpv's eof-reached handling.
            ended = _reachedEnd;
            if (!ended)
            {
                _position = 0;
                _hasVideo = false;
                _hasAudio = false;
            }
        }

        if (!ended)
        {
            Publish(urgent: true);
        }
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        bool loadPending;
        lock (_gate)
        {
            loadPending = _loadPending;
            _loadPending = false;

            // An input that failed to open reports Ended without ever having played; the medium the
            // session is waiting for is terminal either way, so the end is published and the
            // session's reload wait can settle instead of spinning in Transitioning forever.
            if (!loadPending)
            {
                _reachedEnd = true;
                _position = _duration;
                _hasVideo = false;
                _hasAudio = false;
            }
        }

        Publish(urgent: true);
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        // Never swallowed, even while a load is in flight: Stop() of the replaced input is issued
        // synchronously before a load starts, so a leftover error here belongs to the medium the
        // session is waiting on. Swallowing it was how a failed cast sat in Transitioning forever.
        lock (_gate)
        {
            _loadPending = false;
            _error = "libvlc 报告播放错误，媒体无法继续播放。";
        }

        Publish(urgent: true);
    }

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        lock (_gate)
        {
            // The input reports time 0 while it is being torn down after a natural end; forwarding
            // that would rewind the ended medium from its final position back to zero.
            if (_reachedEnd)
            {
                return;
            }

            _position = Math.Max(0, e.Time) / 1000.0;
        }

        Publish(urgent: false);
    }

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        lock (_gate)
        {
            _duration = Math.Max(0, e.Length) / 1000.0;
        }

        Publish(urgent: true);
    }

    private void OnVolumeChanged(object? sender, MediaPlayerVolumeChangedEventArgs e)
    {
        var volume = (int)Math.Round(Math.Clamp(e.Volume, 0, 100));
        lock (_gate)
        {
            _volume = volume;
        }

        Publish(urgent: true);
    }

    private void OnMuted(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _muted = true;
        }

        Publish(urgent: true);
    }

    private void OnUnmuted(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _muted = false;
        }

        Publish(urgent: true);
    }

    private void OnLibVlcLog(object? sender, LogEventArgs e)
    {
        var handler = LogMessage;
        if (handler is null)
        {
            return;
        }

        var level = e.Level switch
        {
            LogLevel.Debug => (0, "debug"),
            LogLevel.Notice => (1, "notice"),
            LogLevel.Warning => (2, "warning"),
            _ => (3, "error"),
        };

        if (level.Item1 < _options.LogVerbosity)
        {
            return;
        }

        handler(this, new VlcLogMessage(level.Item2, $"{e.Module}: {e.Message}"));
    }

    /// <summary>
    /// Reads the actual track counts. Like the mpv adapter's track scan, this decides whether the
    /// medium carries video; audio files with embedded cover art do not count as video here.
    /// </summary>
    private void RefreshTrackPresence()
    {
        var player = _player;
        if (player is null)
        {
            return;
        }

        int video;
        int audio;
        try
        {
            video = player.VideoTrackCount;
            audio = player.AudioTrackCount;
        }
        catch
        {
            // The player may be mid-teardown; the next event re-reads the counts.
            return;
        }

        lock (_gate)
        {
            _hasVideo = video > 0;
            _hasAudio = audio > 0;
        }
    }

    // ---- publishing ---------------------------------------------------------------------------

    private void Publish(bool urgent) => PublishCore(urgent, force: false);

    private void PublishCore(bool urgent, bool force)
    {
        if (_disposed)
        {
            return;
        }

        if (urgent)
        {
            _throttle.Reset();
        }
        else if (!_throttle.ShouldPublish(_clock.Elapsed))
        {
            return;
        }

        VlcTransportSnapshot snapshot;
        string? error;
        bool loadPending;
        bool reachedEnd;
        lock (_gate)
        {
            error = _error;
            loadPending = _loadPending;

            // The end is taken from the event, not the state: libvlc reports Ended for a while, and
            // the event flag is what the position reset was keyed on.
            reachedEnd = _reachedEnd;
        }

        // The remaining libvlc state is read here rather than mirrored field by field: the player
        // object is the authority, and reading it once per publish keeps the mapping free of races
        // between the individual events.
        var player = _player;
        var fromState = player is not null
            ? ReadStateSnapshot(player)
            : new VlcTransportSnapshot(HasError: false, ReachedEnd: false, IsStopped: true,
                IsOpening: false, IsBuffering: false, IsPaused: false, IsPlaying: false);
        snapshot = fromState with { ReachedEnd = reachedEnd };

        // A load in flight is Transitioning regardless of what the stale state reports.
        var desired = loadPending ? PlaybackState.Transitioning : VlcStateMapper.Map(snapshot);

        if (!_stateMachine.TryTransitionTo(desired)
            && error is null)
        {
            // Recovery, same rule as the mpv adapter: a Stop that landed between a load and its
            // events leaves the machine idle while the medium really plays. Route through
            // Transitioning once rather than leaving the renderer claiming Stopped with video up.
            if (desired is PlaybackState.Buffering or PlaybackState.Playing
                && _stateMachine.Current == PlaybackState.Stopped)
            {
                _stateMachine.TryTransitionTo(PlaybackState.Transitioning);
                _stateMachine.TryTransitionTo(desired);
            }
        }

        var status = BuildStatus();

        lock (_publishGate)
        {
            if (!force && status == _lastPublished)
            {
                return;
            }

            _lastPublished = status;
        }

        StatusChanged?.Invoke(this, status);
    }

    private readonly object _publishGate = new();

    private static VlcTransportSnapshot ReadStateSnapshot(MediaPlayer player)
    {
        var state = player.State;
        return new VlcTransportSnapshot(
            HasError: state == VLCState.Error,
            ReachedEnd: state == VLCState.Ended,
            IsStopped: state is VLCState.Stopped or VLCState.NothingSpecial,
            IsOpening: state == VLCState.Opening,
            IsBuffering: state == VLCState.Buffering,
            IsPaused: state == VLCState.Paused,
            IsPlaying: state == VLCState.Playing);
    }

    private PlaybackStatus BuildStatus()
    {
        lock (_gate)
        {
            var kind = _hasVideo ? MediaKind.Video : _hasAudio ? MediaKind.Audio : MediaKind.Unknown;
            return new PlaybackStatus
            {
                State = _stateMachine.Current,
                Media = _media,
                MediaKind = kind,
                Position = TimeSpan.FromSeconds(Math.Max(0, _position)),
                Duration = _duration > 0 ? TimeSpan.FromSeconds(_duration) : null,
                Volume = Math.Clamp(_volume, 0, 100),
                Muted = _muted,
                ErrorMessage = _error,
            };
        }
    }

    // ---- plumbing -----------------------------------------------------------------------------

    /// <summary>
    /// Runs one player command on the thread pool. The renderer session serialises commands, so no
    /// ordering machinery is needed here; the pool hop only keeps a slow native call (Stop, Play on
    /// a network medium) from blocking the caller's own threads.
    /// </summary>
    private async Task RunCommandAsync(CancellationToken cancellationToken, Action action)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await Task.Run(action, cancellationToken).ConfigureAwait(false);
        }
        catch (VLCException ex)
        {
            throw new PlayerCommandException($"vlc command failed: {ex.Message}", ex);
        }
    }

    private MediaPlayer RequirePlayer()
    {
        var player = _player;
        if (player is null)
        {
            throw new ObjectDisposedException(nameof(VlcPlayer));
        }

        return player;
    }

    private LibVLC RequireLibVlc()
    {
        var libVlc = _libVlc;
        if (libVlc is null)
        {
            throw new ObjectDisposedException(nameof(VlcPlayer));
        }

        return libVlc;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>One forwarded libvlc log line.</summary>
public sealed record VlcLogMessage(string Level, string Text);
