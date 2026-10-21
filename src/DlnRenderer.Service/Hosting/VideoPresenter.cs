using System.Collections.Concurrent;
using System.Runtime.InteropServices;

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
    /// <summary>Shows without repainting the overlay, keeping whatever mode it is in.</summary>
    public void Show() => ShowFullscreen();

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
                ApplyFullscreen();
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

        var monitors = MonitorInterop.GetMonitors();
        var current = ReadCurrentBounds();
        var selected = FullscreenGeometry.SelectMonitor(
            monitors,
            FindPreferred(monitors),
            current);

        var target = FullscreenGeometry.FullscreenFor(selected);

        // Idempotent fast path: once the window already covers the selected monitor, nothing is
        // applied, so repeated player events cannot make it flicker or re-take the foreground.
        // The measured rectangle alone is not enough: a window that is positioned but hidden (or was
        // hidden and not yet re-shown) has the fullscreen rectangle too, and skipping ShowWindow
        // there left the overlay invisible for the whole cast.
        if (_visible && IsWindowVisible(_window) && _monitor == selected.Monitor && ReadClientBounds() == target)
        {
            EnsureTopmost();
            return;
        }

        _monitor = selected.Monitor;
        Volatile.Write(ref _presentationState, "fullscreen");

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
                    ApplyFullscreen();
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
                // Escape leaves the overlay, matching every other video player. Everything else,
                // including Alt+Tab and the Win key, is passed to DefWindowProc so Windows keeps
                // working normally.
                if ((int)wParam == VkEscape)
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
