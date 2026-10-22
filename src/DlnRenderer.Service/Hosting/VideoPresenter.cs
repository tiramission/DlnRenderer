using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DlnRenderer.Core.Ipc;
using DlnRenderer.Core.Models;

namespace DlnRenderer.Service.Hosting;

/// <summary>
/// The always-on-top, borderless-fullscreen Win32 window libmpv renders video into.
/// </summary>
/// <remarks>
/// <para>
/// It exists so the service can present video without depending on WinUI or WinForms, and survives
/// the UI process exiting. The window has a single child HWND; mpv is given that child as its
/// <c>wid</c> and draws into it.
/// </para>
/// <para>
/// Window creation and the message loop live on a dedicated STA thread. Every public member may be
/// called from any thread: work is posted to that thread and executed in order, so concurrent DLNA
/// commands, player events and monitor notifications cannot interleave partially applied window
/// state.
/// </para>
/// <para>
/// Fullscreen and topmost are driven exclusively from here. mpv is deliberately not given
/// <c>fullscreen=yes</c> or <c>ontop=yes</c>: two owners of the same window state fight each other
/// on every resize. See <c>docs/architecture.md</c> §13.
/// </para>
/// </remarks>
public sealed class VideoPresenter : IAsyncDisposable, IMonitorEventsSink
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsChild = 0x40000000;

    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwShowNoActivate = 4;

    private const uint WmApp = 0x8000;
    private const uint WmInvoke = WmApp + 1;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmSize = 0x0005;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmDpiChanged = 0x02E0;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmMouseActivate = 0x0021;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSysKeyDown = 0x0104;

    private const int HtClient = 1;
    private const int VkEscape = 0x1B;
    private const nint HwndTop = 0;
    private const nint HwndTopmost = -1;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WndProc _windowProc;
    private readonly Thread _thread;
    private nint _window;
    private nint _child;
    private volatile bool _visible;
    private Exception? _startupError;

    // Fields below are only read or written on the window thread.
    private nint _monitor;
    private string _presentationState = "hidden";
    private bool _displayChangePending;
    private MonitorEventsListener? _monitorListener;

    /// <summary>Where video may be drawn. Replaced wholesale by <see cref="SetTarget"/>.</summary>
    private VideoTarget _target = VideoTarget.Default;

    /// <summary>Window this one is currently a child of, or 0 when it is a standalone popup.</summary>
    private nint _embeddedHost;

    // Last measured client size, published for diagnostics.
    private volatile int _clientWidth;
    private volatile int _clientHeight;

    private VideoPresenter()
    {
        // Hold a strong reference so the native window class never points at a collected delegate.
        _windowProc = WindowProcedure;
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "DlnRenderer video host",
        };
    }

    /// <summary>Handle for mpv's <c>wid</c> option. Available once <see cref="StartAsync"/> completed.</summary>
    public nint ChildHandle => _child;

    /// <summary>Top-level window handle. Exposed for diagnostics and UI-automation tests.</summary>
    public nint WindowHandle => _window;

    public bool IsVisible => _visible;

    /// <summary>Monitor currently covered in fullscreen, as last applied.</summary>
    public nint MonitorHandle => Volatile.Read(ref _monitor);

    public int ClientWidth => _clientWidth;

    public int ClientHeight => _clientHeight;

    /// <summary>Current presentation mode, for diagnostics: <c>hidden</c> or <c>fullscreen</c>.</summary>
    public string PresentationState => Volatile.Read(ref _presentationState);

    /// <summary>
    /// True while the window is actually in the topmost band, read back from Win32 rather than assumed.
    /// </summary>
    public bool IsTopmost
    {
        get
        {
            // GetWindowLongPtr is safe from any thread for a window that still exists; the window may
            // be destroyed concurrently, so a zero handle means "not topmost".
            return TopmostWindow.IsTopmost(_window);
        }
    }

    public static async Task<VideoPresenter> StartAsync(CancellationToken cancellationToken = default)
    {
        var presenter = new VideoPresenter();
        presenter._thread.SetApartmentState(ApartmentState.STA);
        presenter._thread.Start();

        while (!presenter._ready.Wait(10, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (presenter._startupError is not null)
        {
            await presenter.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                "The video presentation window could not be created.",
                presenter._startupError);
        }

        return presenter;
    }

    /// <summary>
    /// Covers the monitor with a borderless, always-on-top window.
    /// </summary>
    /// <remarks>
    /// Idempotent: once the window is already fullscreen on the same monitor, nothing is applied, so
    /// repeated player events cannot make the window flicker or re-take the foreground.
    /// </remarks>
    public void ShowFullscreen() => Run(ApplyFullscreen);

    /// <summary>
    /// Shows using whatever the current <see cref="VideoTarget"/> asks for: embedded in the UI
    /// window when it reserved an area, fullscreen otherwise.
    /// </summary>
    public void Show() => Run(ShowInMode);

    /// <summary>
    /// Replaces the presentation target.
    /// </summary>
    /// <remarks>
    /// The mode, the host window and the reserved rectangle travel together because they are only
    /// meaningful together: an embedded target with no host must fall back to fullscreen, and a
    /// rectangle that changed while the overlay was hidden must be applied on the next show rather
    /// than now. Nothing is applied until the window is visible again.
    /// </remarks>
    public void SetTarget(VideoTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Run(() =>
        {
            _target = target;
            if (_visible)
            {
                ShowInMode();
            }
        });
    }

    /// <summary>Current target, for diagnostics and tests.</summary>
    public VideoTarget Target => _target;

    /// <summary>Host window this presenter is embedded in, or 0 when it is standalone.</summary>
    public nint EmbeddedHost => _embeddedHost;

    /// <summary>
    /// Drops topmost and hides the window; the window and its mpv surface are kept for the next play.
    /// </summary>
    public void Hide() => Run(ApplyHidden);

    /// <summary>
    /// Blocks until every window action queued so far has run.
    /// </summary>
    /// <remarks>
    /// Window work is posted to the window thread, so the state a caller just requested is not
    /// applied when the call returns. Production code deliberately does not wait (a DLNA handler
    /// must not block on the window thread), but tests, diagnostics and the "is the overlay up?"
    /// answer after a play need a defined point where the request has landed.
    /// </remarks>
    /// <returns>True when the queue drained; false if the window never started.</returns>
    public bool WaitForIdle(TimeSpan timeout)
    {
        if (!_thread.IsAlive)
        {
            return false;
        }

        using var done = new ManualResetEventSlim(false);
        Run(done.Set);

        return done.Wait(timeout);
    }

    /// <summary>
    /// Re-applies fullscreen after a display change. No-op unless the window is meant to be visible.
    /// </summary>
    public void RefreshMonitor()
    {
        Run(() =>
        {
            _monitor = 0;
            if (_visible)
            {
                ShowInMode();
            }
        });
    }

    public ValueTask DisposeAsync()
    {
        if (!_thread.IsAlive)
        {
            _ready.Dispose();
            return ValueTask.CompletedTask;
        }

        Run(() =>
        {
            // Report the window as down before tearing it apart, so a caller inspecting the presenter
            // during shutdown never sees "visible" for a window that no longer exists.
            _visible = false;
            _monitor = 0;
            Volatile.Write(ref _presentationState, "hidden");

            _monitorListener?.Dispose();
            _monitorListener = null;

            if (_window != 0)
            {
                PostMessage(_window, WmClose, 0, 0);
            }
        });

        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            // The thread is a background thread; process exit must not hang on it.
            _ready.Dispose();
            return ValueTask.CompletedTask;
        }

        _ready.Dispose();
        return ValueTask.CompletedTask;
    }

    void IMonitorEventsSink.OnDisplayChanged()
    {
        // Posted from a system thread; the window thread re-evaluates once, not per WM_DISPLAYCHANGE.
        if (_window != 0)
        {
            PostMessage(_window, WmDisplayChange, 0, 0);
        }
    }

    private void ThreadMain()
    {
        try
        {
            var instance = GetModuleHandle(null);

            // The class name must be unique per window, not per process: registering a class that is
            // already registered fails with ERROR_CLASS_ALREADY_EXISTS, and the presenter is created
            // more than once in a process (a restarted session, or repeated test runs).
            var className = $"DlnRendererVideoHost_{Environment.ProcessId}_{Interlocked.Increment(ref ClassSequence)}";

            var windowClass = new WindowClassEx
            {
                Size = Marshal.SizeOf<WindowClassEx>(),
                // A popup with no system menu and no frame is the borderless fullscreen form; the
                // redraw styles only cover the brief period before the first frame arrives.
                Style = 0x0001 /* CS_VREDRAW */ | 0x0002 /* CS_HREDRAW */,
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProc),
                Instance = instance,
                ClassName = className,
            };

            if (RegisterClassEx(ref windowClass) == 0)
            {
                throw new InvalidOperationException(
                    $"RegisterClassEx failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            // Created hidden; ApplyFullscreen sizes and shows it. WS_POPUP without WS_CAPTION,
            // WS_THICKFRAME or WS_SYSMENU means no title bar, border or shadow to paint over.
            // WS_EX_TOOLWINDOW keeps the overlay out of the Alt+Tab list: a cast is not a window the
            // user switches to, and an entry that cannot be dismissed with the keyboard only gets in
            // the way. Task switching itself keeps working for every other window.
            _window = CreateWindowEx(
                WsExToolWindow,
                className,
                "DlnRenderer",
                WsPopup | WsClipSiblings | WsClipChildren,
                0,
                0,
                1,
                1,
                0,
                0,
                instance,
                0);

            if (_window == 0)
            {
                throw new InvalidOperationException(
                    $"CreateWindowEx failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            _child = CreateWindowEx(
                0,
                "static",
                "DlnRendererVideo",
                WsChild | WsClipSiblings | WsClipChildren,
                0,
                0,
                1,
                1,
                _window,
                0,
                instance,
                0);

            if (_child == 0)
            {
                throw new InvalidOperationException(
                    $"Video child window creation failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            // Unplugging, docking or changing resolution while video is up must not leave the overlay
            // at stale coordinates. The listener simply posts a message to this thread.
            _monitorListener = new MonitorEventsListener(this);

            _ready.Set();


            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
        }
    }

    private void ApplyFullscreen()
    {
        if (_window == 0 || _child == 0)
        {
            return;
        }

        // Coming back from embedded: the window is still a child of the UI window, and a child has no
        // place in the topmost band. Re-parenting to the desktop has to happen before placement.
        if (_embeddedHost != 0)
        {
            DetachFromHost();
        }

        // Fullscreen covers a monitor; a rounded corner would leave four holes to the desktop.
        ApplyCornerRegion(0, 0, 0);

        var monitors = MonitorInterop.GetMonitors();
        var current = ReadCurrentBounds();
        var selected = FullscreenGeometry.SelectMonitor(
            monitors,
            FindPreferred(monitors),
            current);

        var target = FullscreenGeometry.FullscreenFor(selected);

        // Idempotent fast path: nothing is applied when this would be a no-op, so repeated player
        // events cannot make the window flicker or re-take the foreground.
        // Every observable piece of state is part of the condition - the rectangle alone is not
        // enough. Hiding shrinks the window to 1x1 and hides the mpv surface, so a window that is
        // merely positioned can look "already fullscreen" while its video child stays hidden, which
        // is exactly how the picture went missing after Stop-then-Play.
        if (_visible
            && IsWindowVisible(_window)
            && IsWindowVisible(_child)
            && _monitor == selected.Monitor
            && ReadClientBounds() == target)
        {
            EnsureTopmost();
            return;
        }

        _monitor = selected.Monitor;
        Volatile.Write(ref _presentationState, "fullscreen");

        // The mpv surface is a child window, and hiding the overlay hides it. Showing only the top
        // level would leave a black window with audio playing, so the surface is made visible first;
        // the WM_SIZE that follows sizes it to the client area.
        _ = ShowWindow(_child, SwShow);

        // Because the window is WS_POPUP with no frame, the requested rectangle is also the client
        // rectangle, so the video covers the monitor exactly with no border band. Placement happens
        // first and deliberately *not* as topmost: topmost requires a visible window (see below).
        var flags = SwpShowWindow | SwpFrameChanged;
        if (!IsActiveWindow(_window))
        {
            // Nobody is waiting on this window: never yank focus from the user's foreground app.
            flags |= SwpNoActivate;
        }

        var placed = SetWindowPos(
            _window,
            HwndTop,
            target.Left,
            target.Top,
            target.Width,
            target.Height,
            flags);

        // Now that the window is on screen, topmost is applied and verified. Visibility is published
        // after that, so an observer that sees IsVisible also sees the applied style.
        TopmostApplied = TopmostWindow.Apply(_window, IsActiveWindow(_window));
        _visible = true;

        if (!placed)
        {
            PlacementError = Marshal.GetLastWin32Error();
        }
        else
        {
            PlacementError = 0;
        }
    }

    /// <summary>
    /// Shows the video using the current target: embedded into the UI window, or fullscreen.
    /// </summary>
    /// <remarks>
    /// An embedded target that is missing its host or its rectangle is honoured as fullscreen. That
    /// is the deliberate failure direction: the alternative is a cast that plays with no picture
    /// anywhere, which looks like a broken renderer rather than a window that has not laid out yet.
    /// </remarks>
    private void ShowInMode()
    {
        // The UI said it has nowhere to show the picture (its player page is not on screen).
        if (!_target.Enabled)
        {
            ApplyHidden();
            return;
        }

        if (_target.CanEmbed)
        {
            ApplyEmbedded(_target);
            return;
        }

        // Embedded mode never falls back to the fullscreen overlay.
        //
        // It used to, on the theory that "no picture anywhere" looks broken. In practice the
        // fallback fires in situations the user did not ask for - a page that has not laid out yet,
        // a navigation in progress - and a cast that suddenly covers the whole screen is far more
        // alarming than one that is briefly missing. The fullscreen overlay is only ever used when
        // the mode actually is fullscreen (the default when no UI is attached at all).
        if (_target.Mode == DisplayMode.Embedded)
        {
            ApplyHidden();
            return;
        }

        ApplyFullscreen();
    }

    /// <summary>
    /// Makes this window a child of the UI window and positions it over the reserved area.
    /// </summary>
    /// <remarks>
    /// Re-parenting instead of overlaying a positioned popup is what makes the video behave like part
    /// of the window: it is clipped by it, moves with it, disappears when it is minimised and is
    /// destroyed with it. Coordinates are the host's client area, which is also the space a child
    /// window is positioned in, so nothing has to be translated.
    /// <para>
    /// The mpv surface is a child of this window, so it follows without being touched - no player
    /// restart and no new <c>wid</c> is needed when the mode changes.
    /// </para>
    /// </remarks>
    private void ApplyEmbedded(VideoTarget target)
    {
        if (_window == 0 || _child == 0)
        {
            return;
        }

        var host = (nint)target.HostWindow;

        // The overlay came in topmost. Leave that band *before* re-parenting: once the window is a
        // child the z-order call is meaningless, and the leftover WS_EX_TOPMOST would put the video
        // back over every other window the next time it is detached.
        TopmostWindow.Leave(_window);
        TopmostApplied = false;

        if (_embeddedHost != host)
        {
            if (_embeddedHost != 0)
            {
                DetachFromHost();
            }

            // WS_CHILD before SetParent: the style decides the coordinate space the window lives in,
            // and setting the parent first would briefly place it at screen coordinates inside the
            // host's client area.
            SetStyle(WsChild | WsClipSiblings | WsClipChildren);

            // SetParent returns the previous parent, which is 0 for a window that had none - so
            // "returned 0" alone means nothing. Only the thread's last error distinguishes success
            // from failure, and it must be cleared first because every Win32 call leaves one behind.
            Marshal.SetLastPInvokeError(0);
            var previous = SetParent(_window, host);
            if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
            {
                // Cross-process parenting can be refused (different integrity level, or a host that
                // is not a top-level window). Falling back keeps the cast visible.
                LogPlacementFailure($"SetParent on 0x{host:X} failed", Marshal.GetLastPInvokeError());
                SetStyle(WsPopup | WsClipSiblings | WsClipChildren);
                ApplyFullscreen();
                return;
            }

            _embeddedHost = host;
        }

        _monitor = 0;
        Volatile.Write(ref _presentationState, "embedded");

        // The child is hidden while nothing is presented; showing it first means the WM_SIZE that
        // follows resizes a visible surface rather than a hidden one.
        _ = ShowWindow(_child, SwShow);

        // Not topmost and not activatable: inside the application window the user's own z-order and
        // focus must be left alone.
        _ = SetWindowPos(
            _window,
            0,
            target.X,
            target.Y,
            Math.Max(1, target.Width),
            Math.Max(1, target.Height),
            SwpShowWindow | SwpFrameChanged | SwpNoActivate | SwpNoZOrder);

        // The page draws its cards with rounded corners; a square picture inside that layout looks
        // like a hole punched in the window. The window is clipped to the same shape, which also
        // clips the mpv surface inside it.
        ApplyCornerRegion(Math.Max(1, target.Width), Math.Max(1, target.Height), target.CornerRadius);

        _visible = true;
    }

    /// <summary>
    /// Returns the window to a standalone popup so it survives the UI window closing.
    /// </summary>
    /// <remarks>
    /// A child window is destroyed by Windows when its parent goes away, which would leave the player
    /// drawing into a dead HWND. Every path that leaves embedded mode therefore detaches first -
    /// including the one triggered by the UI disconnecting.
    /// </remarks>
    private void DetachFromHost()
    {
        if (_window == 0 || _embeddedHost == 0)
        {
            _embeddedHost = 0;
            return;
        }

        // Style first: a WS_CHILD window cannot be re-parented to the desktop and keep behaving as a
        // popup, because the desktop is not a parent window.
        SetStyle(WsPopup | WsClipSiblings | WsClipChildren);
        _ = SetParent(_window, 0);
        _embeddedHost = 0;

        // The rectangle is meaningless now (1x1 keeps it off screen without destroying it); the next
        // fullscreen show recomputes it from the monitors.
        _ = SetWindowPos(_window, 0, 0, 0, 1, 1, SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>
    /// Cuts the window to a rounded rectangle, or squares it off again when the radius is 0.
    /// </summary>
    /// <remarks>
    /// A window region clips everything the window paints, including the mpv surface it hosts, which
    /// is why the picture itself gets the corners. The system owns the region once set, so it is not
    /// deleted here; passing 0 removes it and restores the full rectangle.
    /// </remarks>
    private void ApplyCornerRegion(int width, int height, int radius)
    {
        if (_window == 0)
        {
            return;
        }

        if (radius <= 0)
        {
            _ = SetWindowRgn(_window, 0, redraw: true);
            return;
        }

        var region = CreateRoundRectRgn(0, 0, width, height, radius * 2, radius * 2);
        if (region != 0)
        {
            _ = SetWindowRgn(_window, region, redraw: true);
        }
    }

    private void SetStyle(int style)
        => _ = SetWindowLongPtr(_window, GwlStyle, (nint)style);

    /// <summary>Win32 error from the last failed placement, logged once per attempt. Diagnostics only.</summary>
    private void LogPlacementFailure(string what, int error)
    {
        PlacementError = error;
        Volatile.Write(ref _lastPlacementFailure, $"{what} (Win32 error {error})");
    }

    private string? _lastPlacementFailure;

    /// <summary>Description of the last placement failure, or null. Diagnostics only.</summary>
    public string? LastPlacementFailure => Volatile.Read(ref _lastPlacementFailure);

    /// <summary>Win32 error from the last failed fullscreen placement, or 0. Diagnostics only.</summary>
    public int PlacementError { get; private set; }


    /// <summary>True when the overlay is in the topmost band. Diagnostics and tests read this.</summary>
    public bool TopmostApplied { get; private set; }


    private void EnsureTopmost()
    {
        // Re-asserting z-order on an unchanged rectangle does not repaint the video. This runs only
        // on discrete events (show, resize, display change), never on a timer.
        _ = SetWindowPos(
            _window,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | (IsActiveWindow(_window) ? 0 : SwpNoActivate));
    }

    private void ApplyHidden()
    {
        if (_window == 0)
        {
            // No window (startup failure): still record the requested state.
            _visible = false;
            _monitor = 0;
            Volatile.Write(ref _presentationState, "hidden");
            return;
        }

        // Published together with the style change, on the window thread, so an observer can never
        // see "hidden" while topmost is still set, nor "fullscreen" while the window is hidden.
        // The zeroed rectangle is part of that: a hidden window is reported as 0x0 rather than
        // carrying stale fullscreen coordinates that look like a live overlay.
        _visible = false;
        _monitor = 0;
        Volatile.Write(ref _presentationState, "hidden");

        // Drop topmost before hiding so the window does not stay in the topmost list while unused.
        TopmostWindow.Leave(_window);
        TopmostApplied = false;

        if (_child != 0)
        {
            _ = ShowWindow(_child, SwHide);
        }

        // ShowWindow(SW_HIDE) alone is not enough here: while this process is not the foreground
        // process, the call may only change the window's internal show state without touching the
        // screen. Shrinking the window removes it from the screen unconditionally, which is what
        // "the cast is over" has to mean; the next fullscreen recomputes its rectangle anyway.
        _ = SetWindowPos(_window, 0, 0, 0, 1, 1, SwpNoZOrder | SwpNoActivate);
        _ = ShowWindow(_window, SwHide);
    }

    /// <summary>
    /// Monitor the window should stay on: the one it currently covers, if still attached.
    /// </summary>
    private MonitorInfo? FindPreferred(ReadOnlySpan<MonitorInfo> monitors)
    {
        if (_monitor != 0)
        {
            foreach (var monitor in monitors)
            {
                if (monitor.Monitor == _monitor)
                {
                    return monitor;
                }
            }

            // The preferred monitor is gone (unplugged). Forget it so the window can move.
            _monitor = 0;
        }

        return null;
    }

    private ScreenBounds ReadCurrentBounds()
    {
        if (_window != 0 && GetWindowRect(_window, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)
        {
            return new ScreenBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        // Never resized yet: fall back to the monitor Windows thinks the window is nearest to.
        var handle = MonitorInterop.MonitorFromWindow(_window);
        var monitors = MonitorInterop.GetMonitors();
        if (handle != 0)
        {
            foreach (var monitor in monitors)
            {
                if (monitor.Monitor == handle)
                {
                    return monitor.MonitorBounds;
                }
            }
        }

        return new ScreenBounds(0, 0, 1, 1);
    }

    private ScreenBounds ReadClientBounds()
    {
        if (_window == 0)
        {
            return default;
        }

        if (!GetWindowRect(_window, out var rect))
        {
            return default;
        }

        // Client rect in screen coordinates, derived from the window rect so it is directly
        // comparable with the monitor rectangle.
        if (!GetClientRect(_window, out var client))
        {
            return default;
        }

        var offsetX = rect.Left;
        var offsetY = rect.Top;
        return new ScreenBounds(offsetX, offsetY, offsetX + client.Right, offsetY + client.Bottom);
    }

    private void UpdateChildBounds()
    {
        if (_child == 0)
        {
            return;
        }

        _ = GetClientRect(_window, out var client);
        var width = Math.Max(1, client.Right);
        var height = Math.Max(1, client.Bottom);
        _clientWidth = width;
        _clientHeight = height;

        _ = SetWindowPos(
            _child,
            0,
            0,
            0,
            width,
            height,
            SwpNoZOrder | SwpNoActivate);
    }

    private nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmInvoke:
                while (_queue.TryDequeue(out var action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        _startupError ??= ex;
                    }
                }

                return 0;

            case WmSize:
                UpdateChildBounds();
                return 0;

            case WmDisplayChange:
                if (_displayChangePending)
                {
                    return 0;
                }

                // Windows sends a burst of these per resolution change. Re-evaluate once, after the
                // burst settled, instead of resizing several times.
                _displayChangePending = true;
                _ = SetTimer(hwnd, DisplayChangeTimer, 250, 0);
                return 0;

            case WmTimer when wParam == DisplayChangeTimer:
                _ = KillTimer(hwnd, DisplayChangeTimer);
                _displayChangePending = false;
                if (_visible)
                {
                    _monitor = 0;
                    ShowInMode();
                }

                return 0;

            case WmDpiChanged:
                // Per-monitor DPI changed (window dragged across displays or scaling changed).
                // Fullscreen always covers physical pixels, so only the child needs resizing.
                UpdateChildBounds();
                return 0;

            case WmNcHitTest:
                // Treat the whole window as client area so no invisible resize border consumes the
                // picture edge.
                return HtClient;

            case WmMouseActivate:
                return 1; // MA_ACTIVATE: clicking the picture must not route input elsewhere.

            case WmKeyDown:
            case WmSysKeyDown:
                // Escape leaves the standalone overlay, matching every other video player. Everything
                // else, including Alt+Tab and the Win key, is passed to DefWindowProc so Windows keeps
                // working normally.
                //
                // Embedded is the exception: the picture is part of the application window, and
                // tearing it down behind the session's back would make it reappear on the next status
                // event. The UI owns the keys there - its floating bar carries the exit.
                if ((int)wParam == VkEscape && _embeddedHost == 0)
                {
                    ApplyHidden();
                    return 0;
                }

                return DefWindowProc(hwnd, message, wParam, lParam);

            case WmClose:
                DestroyWindow(hwnd);
                return 0;

            case WmDestroy:
                PostQuitMessage(0);
                return 0;

            default:
                return DefWindowProc(hwnd, message, wParam, lParam);
        }
    }


    private void Run(Action action)
    {
        if (!_thread.IsAlive)
        {
            return;
        }

        _queue.Enqueue(action);

        // Posting against handle 0 targets the thread's queue, which also works before _window is set.
        _ = PostMessage(_window, WmInvoke, 0, 0);
    }

    private const long WsExTopmost = 0x00000008L;
    private const int WsExToolWindow = 0x00000080;
    private const int GwlExStyle = -20;
    private const int GwlStyle = -16;
    private const uint WmTimer = 0x0113;
    private const nint DisplayChangeTimer = 1;

    private static int ClassSequence;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public int Size;
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string? ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Value;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    /// <summary>Re-parents a window. Works across processes, which is how embedding is implemented.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetParent(nint child, nint newParent);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int widthEllipse,
        int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("user32.dll")]
    private static extern nint SetTimer(nint hwnd, nint timerId, uint interval, nint procedure);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(nint hwnd, nint timerId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, nint hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    private bool IsActiveWindow(nint window) => GetForegroundWindow() == window;
}
