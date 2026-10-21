using System.Runtime.InteropServices;

namespace DlnRenderer.Service.Hosting;

/// <summary>Receives display configuration notifications on a system thread.</summary>
internal interface IMonitorEventsSink
{
    void OnDisplayChanged();
}

/// <summary>
/// Hidden window that forwards <c>WM_DISPLAYCHANGE</c> and <c>WM_SETTINGCHANGE</c> to its sink.
/// </summary>
/// <remarks>
/// A window must exist to receive these, and it must not be the visible overlay: the overlay is
/// hidden between casts, which would silently stop display notifications exactly when the next one
/// matters. The listener is created on the overlay's own thread, so callbacks are marshalled there
/// and no cross-thread window calls are needed. This replaces polling, which would either miss
/// changes or burn CPU waiting for them.
/// </remarks>
internal sealed class MonitorEventsListener : IDisposable
{
    private const uint WmDisplayChange = 0x007E;
    private const uint WmSettingChange = 0x001A;
    private const string ClassName = "DlnRendererMonitorEvents";

    private static readonly object ClassGate = new();
    private static bool _classRegistered;
    private static readonly WndProc Procedure = OnMessage;

    /// <summary>
    /// The sink of the single live listener. The class is process-global but the service creates one
    /// listener, so a static is the simplest correct routing table; a second listener would replace
    /// this value, and both windows call the same thread anyway.
    /// </summary>
    private static IMonitorEventsSink? _sink;

    private nint _window;

    public MonitorEventsListener(IMonitorEventsSink sink)
    {
        EnsureClassRegistered();
        _sink = sink;

        _window = CreateWindowEx(
            0,
            ClassName,
            string.Empty,
            0, // not WS_VISIBLE: there is no desktop presence
            0,
            0,
            0,
            0,
            0,
            0,
            GetModuleHandle(null),
            0);
    }

    public void Dispose()
    {
        if (_window == 0)
        {
            return;
        }

        _ = DestroyWindow(_window);
        _window = 0;
        _sink = null;
    }

    private static void EnsureClassRegistered()
    {
        lock (ClassGate)
        {
            if (_classRegistered)
            {
                return;
            }

            var windowClass = new WindowClassEx
            {
                Size = Marshal.SizeOf<WindowClassEx>(),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(Procedure),
                Instance = GetModuleHandle(null),
                ClassName = ClassName,
            };

            _ = RegisterClassEx(ref windowClass);
            _classRegistered = true;
        }
    }

    private static nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message is WmDisplayChange or WmSettingChange)
        {
            _sink?.OnDisplayChanged();
            return 0;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

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
    private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
