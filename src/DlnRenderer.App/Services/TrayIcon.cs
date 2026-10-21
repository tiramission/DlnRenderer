using System.Runtime.InteropServices;

namespace DlnRenderer.Services;

/// <summary>
/// Tray icon and menu for the UI process.
/// </summary>
/// <remarks>
/// WinUI 3 has no built-in tray control, and pulling in a UI framework just for a tray icon would be
/// a large dependency for one feature. This uses the documented <c>Shell_NotifyIcon</c> API with a
/// message-only window on the UI thread, so no extra message loop is created and every callback
/// stays on the thread that owns the window.
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int WmTrayCallback = WmApp + 1;
    private const int WmLButtonDoubleClick = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const int WmNull = 0x0000;

    private const int NimAdd = 0;
    private const int NimModify = 1;
    private const int NimDelete = 2;
    private const int NifMessage = 0x0001;
    private const int NifIcon = 0x0002;
    private const int NifTip = 0x0004;

    private const int MfString = 0x0000;
    private const int MfSeparator = 0x0800;
    private const int MfGrayed = 0x0001;

    private const int TpmRightButton = 0x0002;
    private const int TpmReturnCommand = 0x0100;

    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x0010;
    private const uint DefaultSize = 0x0040;

    private const int MenuOpen = 1001;
    private const int MenuExit = 1002;
    private const int TrayIconId = 1;

    private const string TrayWindowClass = "DlnRenderer.TrayWindow";

    private readonly WndProc _windowProcedure;
    private readonly IntPtr _window;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private string _title = "DlnRenderer";
    private string _status = "待机";
    private bool _added;
    private bool _disposed;

    /// <summary>Raised for a double click or for the "open" menu item.</summary>
    public event Action? OpenRequested;

    /// <summary>Raised for the "exit" menu item.</summary>
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _windowProcedure = HandleMessage;
        var module = GetModuleHandle(null);

        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            WindowProcedure = _windowProcedure,
            Instance = module,
            ClassName = TrayWindowClass,
        };

        if (RegisterClassEx(ref windowClass) == 0)
        {
            throw new InvalidOperationException(
                $"无法注册托盘窗口类（错误 {Marshal.GetLastWin32Error()}）。");
        }

        // A message-only window has no visual presence; it exists to receive the tray callbacks.
        _window = CreateWindowEx(
            0,
            TrayWindowClass,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            new IntPtr(-3),
            IntPtr.Zero,
            module,
            IntPtr.Zero);

        if (_window == IntPtr.Zero)
        {
            UnregisterClass(TrayWindowClass, module);
            throw new InvalidOperationException(
                $"无法创建托盘窗口（错误 {Marshal.GetLastWin32Error()}）。");
        }

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        _icon = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 0, 0, LoadFromFile | DefaultSize);
        if (_icon != IntPtr.Zero)
        {
            _ownsIcon = true;
        }
        else
        {
            // The application icon is a fallback: a tray without an icon is invisible.
            _icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        }

        AddIcon();
    }

    /// <summary>Updates the tooltip shown on hover. Call from the UI thread.</summary>
    public void SetState(string title, string status)
    {
        if (_disposed)
        {
            return;
        }

        _title = title;
        _status = status;

        if (!_added)
        {
            return;
        }

        var data = CreateData();
        data.Flags = NifTip;
        data.Tip = BuildTip();
        ShellNotifyIcon(NimModify, ref data);
    }

    private string BuildTip()
    {
        var tip = $"{_title} — {_status}";
        return tip.Length > 120 ? tip[..120] : tip;
    }

    private void AddIcon()
    {
        var data = CreateData();
        data.Flags = NifMessage | NifIcon | NifTip;
        data.CallbackMessage = WmTrayCallback;
        data.Icon = _icon;
        data.Tip = BuildTip();

        _added = ShellNotifyIcon(NimAdd, ref data);
        if (!_added)
        {
            throw new InvalidOperationException("无法添加托盘图标。");
        }
    }

    private NotifyIconData CreateData() => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        Window = _window,
        Id = TrayIconId,
        Tip = string.Empty,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private IntPtr HandleMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (message == WmTrayCallback)
            {
                var mouseMessage = (int)(lParam.ToInt64() & 0xFFFF);
                if (mouseMessage == WmLButtonDoubleClick)
                {
                    OpenRequested?.Invoke();
                }
                else if (mouseMessage is WmRButtonUp or WmContextMenu)
                {
                    ShowMenu();
                }

                return IntPtr.Zero;
            }

            return DefWindowProc(window, message, wParam, lParam);
        }
        catch
        {
            // A exception escaping a window procedure would take the UI down.
            return IntPtr.Zero;
        }
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MfString, MenuOpen, "打开主窗口");
            AppendMenu(menu, MfString | MfGrayed, 0, $"{_title} — {_status}");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, MenuExit, "退出应用");

            GetCursorPos(out var cursor);

            // Required by the shell: the owner window must be foreground or the menu will not
            // dismiss when the user clicks elsewhere.
            SetForegroundWindow(_window);

            var command = TrackPopupMenu(
                menu,
                TpmReturnCommand | TpmRightButton,
                cursor.X,
                cursor.Y,
                0,
                _window,
                IntPtr.Zero);

            PostMessage(_window, WmNull, IntPtr.Zero, IntPtr.Zero);

            if (command == MenuOpen)
            {
                OpenRequested?.Invoke();
            }
            else if (command == MenuExit)
            {
                ExitRequested?.Invoke();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_added)
        {
            var data = CreateData();
            ShellNotifyIcon(NimDelete, ref data);
            _added = false;
        }

        if (_window != IntPtr.Zero)
        {
            DestroyWindow(_window);
        }

        UnregisterClass(TrayWindowClass, GetModuleHandle(null));

        if (_ownsIcon && _icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }
    }

    // ---- interop ---------------------------------------------------------------------------

    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public WndProc? WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public int Id;
        public int Flags;
        public int CallbackMessage;
        public IntPtr Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public int State;
        public int StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public int TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public int InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellNotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(
        IntPtr instance,
        string name,
        uint type,
        int width,
        int height,
        uint load);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, int flags, int id, string? item);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(
        IntPtr menu,
        int flags,
        int x,
        int y,
        int reserved,
        IntPtr window,
        IntPtr rectangle);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
