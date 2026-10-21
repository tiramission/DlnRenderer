using System.Diagnostics;
using System.Runtime.InteropServices;
using DlnRenderer.Core.Interfaces;
using DlnRenderer.Core.Models;
using DlnRenderer.Core.State;

namespace DlnRenderer.Player.Mpv;

/// <summary>
/// libmpv-backed implementation of <see cref="IMediaPlayer"/>. All native calls live behind this
/// type: callers never touch libmpv directly.
/// </summary>
/// <remarks>
/// Threading: mpv's own event thread is created here and is the only place native events are read.
/// <see cref="StatusChanged"/> is raised from that thread, so UI consumers must marshal to their
/// dispatcher. Commands are executed on the thread pool so a UI thread is never blocked.
/// </remarks>
public sealed class MpvPlayer : IMediaPlayer
{
    private readonly MpvPlayerOptions _options;
    private readonly PlaybackStateMachine _stateMachine = new();
    private readonly PositionThrottle _throttle;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _eventThread;
    private readonly object _gate = new();
    private readonly object _publishGate = new();

    private IntPtr _context;
    private volatile bool _stopping;
    private bool _disposed;

    // Signals mirrored from mpv, guarded by _gate.
    private bool _idleActive = true;
    private bool _loadPending;
    private int _loadedEntryId;
    private int _lastRequestedEntryId;
    private int _entryCounter;
    private bool _paused;
    private bool _coreIdle;
    private bool _hasVideo;
    private bool _hasAudio;
    private double _position;
    private double _duration;
    private double _volume = 100;
    private bool _muted;
    private string? _title;
    private string? _error;

    private MediaInfo? _media;
    private PlaybackStatus _lastPublished = PlaybackStatus.Empty;

    public MpvPlayer(MpvPlayerOptions? options = null)
    {
        _options = options ?? new MpvPlayerOptions();
        _throttle = new PositionThrottle(_options.PositionUpdateInterval);

        try
        {
            _context = MpvNative.mpv_create();
        }
        catch (DllNotFoundException ex)
        {
            throw new MpvInitializationException(
                $"Could not load {MpvRuntime.LibraryFileName}. Expected it next to the executable at '{MpvRuntime.ExpectedPath}'. " +
                "Run scripts/fetch-libmpv.ps1 or see docs/development.md.", ex);
        }
        catch (BadImageFormatException ex)
        {
            throw new MpvInitializationException(
                $"{MpvRuntime.LibraryFileName} has the wrong architecture for this process. An x64 build is required.", ex);
        }

        if (_context == IntPtr.Zero)
        {
            throw new MpvInitializationException("mpv_create returned a null handle.");
        }

        try
        {
            ApplyOptions(_context, _options);
            Check(MpvNative.mpv_initialize(_context), "initialize");

            if (!string.IsNullOrEmpty(_options.LogLevel))
            {
                MpvNative.mpv_request_log_messages(_context, _options.LogLevel);
            }

            ObserveProperties(_context);
        }
        catch
        {
            MpvNative.mpv_terminate_destroy(_context);
            _context = IntPtr.Zero;
            throw;
        }

        _eventThread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "mpv-event-loop",
        };
        _eventThread.Start();
    }

    public event EventHandler<PlaybackStatus>? StatusChanged;

    /// <summary>Forwarded mpv log messages at or above the configured level.</summary>
    public event EventHandler<MpvLogMessage>? LogMessage;

    public async Task LoadAsync(MediaInfo media, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);
        ThrowIfDisposed();

        cancellationToken.ThrowIfCancellationRequested();

        if (!media.MediaUri.IsAbsoluteUri)
        {
            throw new ArgumentException("Media URI must be absolute.", nameof(media));
        }

        // Local files are passed as native paths; remote URIs (DLNA control points) are passed through.
        var target = media.MediaUri.IsFile ? media.MediaUri.LocalPath : media.MediaUri.AbsoluteUri;

        lock (_gate)
        {
            _media = media;
            _error = null;
            _position = 0;
            _duration = 0;
            _hasVideo = false;
            _hasAudio = false;
            _title = media.Title;
            // Until mpv reports FILE_LOADED, idle-active still describes the previous state.
            _loadPending = true;
        }

        // Mark the load as in progress *before* issuing loadfile. mpv emits FILE_LOADED from its own
        // thread, and for a replayed medium that can arrive before this method returns; the state
        // machine would then still be in Stopped and reject the resulting transition to Paused,
        // leaving the player stuck reporting Stopped with a file loaded and paused.
        _stateMachine.TryTransitionTo(PlaybackState.Stopped);
        _stateMachine.TryTransitionTo(PlaybackState.Transitioning);

        var context = RequireContext();
        try
        {
            await Task.Run(
                () =>
                {
                    ThrowIfDisposed();
                    // Start paused so SetAVTransportURI-style loading does not begin playback by itself.
                    Check(MpvNative.SetFlag(context, "pause", true), "pause");
                    // libmpv numbers every file it opens, monotonically: playlist-entry-id 1, 2, 3...
                    // The id the next load will receive is therefore the count before it. End-of-file
                    // events carrying a smaller id describe a medium that has already been replaced,
                    // which is exactly the stale Stop/EOF report that must not overwrite the state of
                    // the file being loaded now.
                    var nextEntry = 0;
                    lock (_gate)
                    {
                        nextEntry = _entryCounter + 1;
                        _entryCounter = nextEntry;
                    }

                    Check(MpvNative.Command(context, "loadfile", target, "replace"), "loadfile");

                    lock (_gate)
                    {
                        _lastRequestedEntryId = nextEntry;
                    }

                    // Force the notification now that the load is really underway. Replaying the same
                    // media rebuilds a status identical to the last published one, and the
                    // de-duplicating Publish would drop the Transitioning edge; a caller waiting for
                    // the load to land would then wait for a state change that never comes.
                    ForcePublish();
                },
                cancellationToken).ConfigureAwait(false);
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
        var context = RequireContext();
        return RunCommandAsync("pause", cancellationToken, () => MpvNative.SetFlag(context, "pause", false));
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        var context = RequireContext();
        return RunCommandAsync("pause", cancellationToken, () => MpvNative.SetFlag(context, "pause", true));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        var context = RequireContext();

        // An explicit stop wins over an in-flight load; otherwise the ignored end-file event
        // for the replaced file would leave the state machine stuck in Transitioning.
        lock (_gate)
        {
            _loadPending = false;

            // Forget the loaded entry: everything before this stop is superseded. The next load
            // records its own id, and an end-of-file that arrives in between describes the medium
            // that was just stopped.
            _loadedEntryId = 0;
        }

        return RunCommandAsync("stop", cancellationToken, () => MpvNative.Command(context, "stop"));
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (position < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Seek position cannot be negative.");
        }

        var seconds = position.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var context = RequireContext();

        await RunCommandAsync(
            "seek",
            cancellationToken,
            () => MpvNative.Command(context, "seek", seconds, "absolute+exact")).ConfigureAwait(false);
    }

    public Task SetVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        if (volume is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be between 0 and 100.");
        }

        var context = RequireContext();
        return RunCommandAsync(
            "volume",
            cancellationToken,
            () => MpvNative.SetDouble(context, "volume", volume));
    }

    public Task SetMuteAsync(bool muted, CancellationToken cancellationToken)
    {
        var context = RequireContext();
        return RunCommandAsync("mute", cancellationToken, () => MpvNative.SetFlag(context, "mute", muted));
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
        _stopping = true;

        if (_context != IntPtr.Zero)
        {
            MpvNative.mpv_wakeup(_context);
        }

        // Give the event thread a bounded chance to leave mpv_wait_event before the handle dies.
        if (_eventThread is { IsAlive: true } && !_eventThread.Join(TimeSpan.FromSeconds(3)))
        {
            Log("warning", "mpv event thread did not stop within 3s; terminating the handle regardless.");
        }

        var context = Interlocked.Exchange(ref _context, IntPtr.Zero);
        if (context != IntPtr.Zero)
        {
            MpvNative.mpv_terminate_destroy(context);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static void ApplyOptions(IntPtr context, MpvPlayerOptions options)
    {
        // Deterministic behaviour: ignore the user's mpv.conf and terminal input.
        SetOption(context, "config", "no");
        SetOption(context, "terminal", "no");
        SetOption(context, "input-default-bindings", "no");
        SetOption(context, "input-vo-keyboard", "no");
        SetOption(context, "osc", "no");
        SetOption(context, "osd-level", "0");
        SetOption(context, "idle", "yes");
        SetOption(context, "keep-open", "no");

        // Window state belongs to the host (VideoPresenter), which owns the HWND and the message
        // loop. Letting mpv also manage fullscreen/topmost makes two systems fight over the same
        // window geometry and focus on every resize. Pinned off so a future mpv default cannot
        // silently reintroduce the conflict.
        SetOption(context, "fullscreen", "no");
        SetOption(context, "ontop", "no");
        SetOption(context, "border", "no");
        SetOption(context, "keepaspect", "yes");
        SetOption(context, "keepaspect-window", "yes");

        SetOption(context, "hwdec", options.HardwareDecoding ? "auto-safe" : "no");

        if (!string.IsNullOrEmpty(options.AudioOutput))
        {
            SetOption(context, "ao", options.AudioOutput);
        }

        if (options.VideoWindowHandle != 0)
        {
            SetOption(context, "wid", options.VideoWindowHandle.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SetOption(context, "vo", options.VideoOutput ?? "gpu");
            SetOption(context, "gpu-api", "d3d11");
        }
        else if (!string.IsNullOrEmpty(options.VideoOutput))
        {
            SetOption(context, "vo", options.VideoOutput);
        }
        else
        {
            SetOption(context, "vo", "null");
        }

        void SetOption(IntPtr ctx, string name, string value)
        {
            var result = MpvNative.mpv_set_option_string(ctx, name, value);
            if (result < 0)
            {
                throw new MpvInitializationException(
                    $"mpv rejected option '{name}={value}': {MpvNative.ErrorText(result)}");
            }
        }
    }

    private static void ObserveProperties(IntPtr context)
    {
        var properties = new (string Name, MpvFormat Format)[]
        {
            ("pause", MpvFormat.Flag),
            ("idle-active", MpvFormat.Flag),
            ("core-idle", MpvFormat.Flag),
            ("eof-reached", MpvFormat.Flag),
            ("time-pos", MpvFormat.Double),
            ("duration", MpvFormat.Double),
            ("volume", MpvFormat.Double),
            ("mute", MpvFormat.Flag),
            ("media-title", MpvFormat.String),
        };

        foreach (var (name, format) in properties)
        {
            var result = MpvNative.mpv_observe_property(context, 0, name, (int)format);
            if (result < 0)
            {
                throw new MpvInitializationException(
                    $"mpv could not observe property '{name}': {MpvNative.ErrorText(result)}");
            }
        }
    }

    private void EventLoop()
    {
        while (!_stopping)
        {
            var context = _context;
            if (context == IntPtr.Zero)
            {
                return;
            }

            var eventPtr = MpvNative.mpv_wait_event(context, 0.1);
            if (eventPtr == IntPtr.Zero)
            {
                continue;
            }

            var nativeEvent = Marshal.PtrToStructure<MpvEvent>(eventPtr);
            switch (nativeEvent.EventId)
            {
                case MpvEventId.None:
                    continue;
                case MpvEventId.Shutdown:
                    return;
                case MpvEventId.PropertyChange:
                    HandleProperty(nativeEvent);
                    break;
                case MpvEventId.FileLoaded:
                    // The file that just became current supersedes any load still considered pending.
                    // FILE_LOADED is the definitive "this file is loaded now" event, and leaving the
                    // flag set would keep the status at Transitioning forever when a Stop raced the
                    // load and its events were therefore not observed.
                    lock (_gate)
                    {
                        _idleActive = false;
                        _loadPending = false;
                        _error = null;

                        // Record which playlist entry is current, so a late end-of-file event for the
                        // medium that a Stop just terminated can be recognised and ignored.
                        var position = MpvNative.GetString(context, "playlist-pos");
                        _loadedEntryId = int.TryParse(position, out var loadedId) ? loadedId : 0;
                    }

                    RefreshTrackPresence(context);
                    Publish(urgent: true);
                    break;
                case MpvEventId.VideoReconfig:
                case MpvEventId.AudioReconfig:
                    RefreshTrackPresence(context);
                    Publish(urgent: true);
                    break;
                case MpvEventId.StartFile:
                    Publish(urgent: true);
                    break;
                case MpvEventId.EndFile:
                    HandleEndFile(nativeEvent);
                    break;
                case MpvEventId.PlaybackRestart:
                    Publish(urgent: true);
                    break;
                case MpvEventId.LogMessage:
                    HandleLogMessage(nativeEvent);
                    break;
            }
        }
    }

    private void HandleProperty(MpvEvent nativeEvent)
    {
        if (nativeEvent.Data == IntPtr.Zero)
        {
            return;
        }

        var property = Marshal.PtrToStructure<MpvEventProperty>(nativeEvent.Data);
        var name = Marshal.PtrToStringUTF8(property.Name);
        var data = property.Data;
        if (name is null || data == IntPtr.Zero)
        {
            return;
        }

        var urgent = true;

        lock (_gate)
        {
            switch (name)
            {
                case "time-pos":
                    _position = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(data));
                    urgent = false;
                    break;
                case "duration":
                    _duration = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(data));
                    break;
                case "volume":
                    _volume = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(data));
                    break;
                case "pause":
                    _paused = Marshal.ReadInt32(data) != 0;
                    break;
                case "idle-active":
                    _idleActive = Marshal.ReadInt32(data) != 0;
                    break;
                case "core-idle":
                    _coreIdle = Marshal.ReadInt32(data) != 0;
                    break;
                case "eof-reached":
                    // End of file is reflected by idle-active; nothing extra to record here.
                    if (Marshal.ReadInt32(data) != 0)
                    {
                        _position = _duration;
                    }

                    break;
                case "mute":
                    _muted = Marshal.ReadInt32(data) != 0;
                    break;
                case "media-title":
                    _title = Marshal.PtrToStringUTF8(data);
                    break;
                default:
                    return;
            }
        }

        Publish(urgent);
    }

    /// <summary>
    /// Reads the actual track list. The <c>vid</c>/<c>aid</c> properties cannot be used for this:
    /// they are selection preferences ("auto") and stay non-"no" even for audio-only media.
    /// Album art is a video track in mpv, so it must be ignored when classifying the media.
    /// </summary>
    private void RefreshTrackPresence(IntPtr context)
    {
        var hasVideo = false;
        var hasAudio = false;

        if (int.TryParse(MpvNative.GetString(context, "track-list/count"), out var count) && count > 0)
        {
            for (var i = 0; i < count; i++)
            {
                var type = MpvNative.GetString(context, $"track-list/{i}/type");
                if (string.Equals(type, "video", StringComparison.OrdinalIgnoreCase))
                {
                    var albumArt = MpvNative.GetString(context, $"track-list/{i}/albumart");
                    if (string.Equals(albumArt, "yes", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    hasVideo = true;
                }
                else if (string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase))
                {
                    hasAudio = true;
                }
            }
        }

        lock (_gate)
        {
            _hasVideo = hasVideo;
            _hasAudio = hasAudio;
        }
    }

    private void HandleEndFile(MpvEvent nativeEvent)
    {
        if (nativeEvent.Data == IntPtr.Zero)
        {
            return;
        }

        var endFile = Marshal.PtrToStructure<MpvEventEndFile>(nativeEvent.Data);
        bool loadPending;
        bool superseded;
        lock (_gate)
        {
            loadPending = _loadPending;

            // Whether this end-of-file describes the medium the renderer is now working with.
            // Replacing media numbers the new file's playlist entry; an event carrying the shorter
            // (earlier) id belongs to the previous medium - typically the one Stop just terminated -
            // and must not be allowed to report Stopped or Error for its successor.
            var currentEntry = _lastRequestedEntryId;
            var staleEntry = currentEntry > 0
                && endFile.PlaylistEntryId > 0
                && endFile.PlaylistEntryId < currentEntry;

            superseded = staleEntry
                && endFile.Reason != MpvEndFileReason.Error
                && endFile.Error >= 0;
        }

        // Return outside the lock: returning inside would leave _gate held and deadlock every
        // subsequent status read.
        if (superseded)
        {
            // Do not publish either: the status snapshot still carries the stale idle/paused flags
            // of the old entry, and publishing would drag a load that is already Transitioning back
            // to Stopped. The newer load publishes its own transition when it lands.
            return;
        }

        if (endFile.Reason == MpvEndFileReason.Error || endFile.Error < 0)
        {
            var reason = endFile.Error < 0 ? MpvNative.ErrorText(endFile.Error) : "playback failed";

            // A genuine failure of the medium that is being loaded now is reported; one belonging to
            // the medium that was already replaced is not.
            bool staleError;
            lock (_gate)
            {
                staleError = _lastRequestedEntryId > 0
                    && endFile.PlaylistEntryId > 0
                    && endFile.PlaylistEntryId < _lastRequestedEntryId;
            }

            if (staleError)
            {
                return;
            }
            lock (_gate)
            {
                _loadPending = false;
                _error = reason;
                _paused = false;
                _idleActive = false;
            }
        }
        else if (loadPending)
        {
            // A load is in flight. This branch used to assume "the old file ended because it was
            // replaced", but the end-file event says nothing of the sort: when the *new* file is
            // unreadable (server gone, unsupported codec), mpv reports the error here, and swallowing
            // it left the renderer reporting Transitioning forever with no media and no picture.
            // Treat it as a terminal event for the current load; a follow-up FILE_LOADED from a
            // still-valid newer load resets the error again.
            lock (_gate)
            {
                _loadPending = false;
                _idleActive = true;
                _paused = false;
            }
        }
        else
        {
            lock (_gate)
            {
                _idleActive = true;
                _paused = false;
                _position = 0;
                _hasVideo = false;
                _hasAudio = false;
            }
        }

        Publish(urgent: true);
    }

    private void HandleLogMessage(MpvEvent nativeEvent)
    {
        var handler = LogMessage;
        if (handler is null || nativeEvent.Data == IntPtr.Zero)
        {
            return;
        }

        var message = Marshal.PtrToStructure<MpvEventLogMessage>(nativeEvent.Data);
        var level = message.Level == IntPtr.Zero ? "log" : Marshal.PtrToStringUTF8(message.Level) ?? "log";
        var text = message.Text == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(message.Text) ?? string.Empty;
        handler(this, new MpvLogMessage(level, text));
    }

    private void Publish(bool urgent) => PublishCore(urgent, force: false);

    /// <summary>
    /// Publishes regardless of whether the status equals the last one.
    /// </summary>
    /// <remarks>
    /// Used for edges that subscribers must observe even when every field is unchanged - notably the
    /// start of a load: replaying the same media produces a status identical to the previously
    /// published one, and de-duplication would hide the Transitioning edge.
    /// </remarks>
    private void ForcePublish() => PublishCore(urgent: true, force: true);

    private void PublishCore(bool urgent, bool force)
    {
        if (_disposed)
        {
            return;
        }

        if (!urgent)
        {
            if (!_throttle.ShouldPublish(_clock.Elapsed))
            {
                return;
            }
        }
        else
        {
            _throttle.Reset();
        }

        MpvTransportSnapshot snapshot;
        string? error;
        bool loadPending;
        lock (_gate)
        {
            snapshot = new MpvTransportSnapshot(
                IsIdle: _idleActive,
                IsPaused: _paused,
                IsBuffering: _coreIdle,
                HasError: _error is not null);
            error = _error;
            loadPending = _loadPending;
        }

        // A load in flight is Transitioning regardless of the stale idle flag.
        var desired = loadPending ? PlaybackState.Transitioning : MpvStateMapper.Map(snapshot);

        // Never bypass the domain transition rules: an impossible jump (e.g. Error -> Playing)
        // is rejected here and the previous state is kept. The one exception is recovery: if the
        // player reports real playback while the machine is idle, that is a medium which is playing
        // and therefore in Transitioning. This happens when a Stop lands between a loadfile and its
        // events, leaving "idle" recorded for a file that then plays; refusing it would leave the
        // renderer claiming Stopped with video on screen.
        if (!_stateMachine.TryTransitionTo(desired))
        {
            if (desired is PlaybackState.Buffering or PlaybackState.Playing
                && _stateMachine.Current == PlaybackState.Stopped
                && error is null)
            {
                _stateMachine.TryTransitionTo(PlaybackState.Transitioning);
                _stateMachine.TryTransitionTo(desired);
            }
            else if (error is null)
            {
                Log("debug", $"rejected transport transition to {desired}");
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

    private PlaybackStatus BuildStatus()
    {
        PlaybackStatus status;
        lock (_gate)
        {
            var kind = _hasVideo ? MediaKind.Video : _hasAudio ? MediaKind.Audio : MediaKind.Unknown;
            status = new PlaybackStatus
            {
                State = _stateMachine.Current,
                Media = _media,
                MediaKind = kind,
                Position = TimeSpan.FromSeconds(Math.Max(0, _position)),
                Duration = _duration > 0 ? TimeSpan.FromSeconds(_duration) : null,
                Volume = (int)Math.Round(Math.Clamp(_volume, 0, 100)),
                Muted = _muted,
                ErrorMessage = _error,
            };
        }

        return status;
    }

    private async Task RunCommandAsync(string label, CancellationToken cancellationToken, Func<int> action)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await Task.Run(
            () =>
            {
                ThrowIfDisposed();
                Check(action(), label);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Captures the current handle and rejects calls after disposal. Commands are built against
    /// this captured value so a concurrent DisposeAsync cannot zero the handle mid-call.
    /// </summary>
    private IntPtr RequireContext()
    {
        var context = _context;
        if (context == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(MpvPlayer));
        }

        return context;
    }

    private static void Check(int result, string operation)
    {
        if (result < 0)
        {
            throw new MpvCommandException(operation, result, MpvNative.ErrorText(result));
        }
    }

    private void Log(string level, string text)
        => LogMessage?.Invoke(this, new MpvLogMessage(level, text));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>One forwarded libmpv log line.</summary>
public sealed record MpvLogMessage(string Level, string Text);
