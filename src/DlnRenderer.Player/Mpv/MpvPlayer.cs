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

        _stateMachine.TryTransitionTo(PlaybackState.Transitioning);
        Publish(urgent: true);

        var context = RequireContext();
        try
        {
            await Task.Run(
                () =>
                {
                    ThrowIfDisposed();
                    // Start paused so SetAVTransportURI-style loading does not begin playback by itself.
                    Check(MpvNative.SetFlag(context, "pause", true), "pause");
                    Check(MpvNative.Command(context, "loadfile", target, "replace"), "loadfile");
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
                    lock (_gate)
                    {
                        _idleActive = false;
                        _loadPending = false;
                        _error = null;
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
                    _stateMachine.TryTransitionTo(PlaybackState.Transitioning);
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
        lock (_gate)
        {
            loadPending = _loadPending;
        }

        if (endFile.Reason == MpvEndFileReason.Error || endFile.Error < 0)
        {
            var reason = endFile.Error < 0 ? MpvNative.ErrorText(endFile.Error) : "playback failed";
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
            // Replacing a playing file ends the old one first; the new load is still in flight.
            // Stay in Transitioning until FILE_LOADED arrives (or an error end-file above).
            return;
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

    private void Publish(bool urgent)
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
        // is rejected here and the previous state is kept.
        if (!_stateMachine.TryTransitionTo(desired) && error is null)
        {
            Log("debug", $"rejected transport transition to {desired}");
        }

        var status = BuildStatus();

        lock (_publishGate)
        {
            if (status == _lastPublished)
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
