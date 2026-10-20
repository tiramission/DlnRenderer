using System.Runtime.InteropServices;

namespace DlnRenderer.Player.Mpv;

/// <summary>
/// A plain Win32 child window that libmpv renders video into.
/// </summary>
/// <remarks>
/// WinUI 3 has no <c>HwndHost</c> equivalent, so the video surface is a real child HWND owned by
/// the WinUI top-level window. mpv draws into it directly (no D3D/Composition interop layer).
/// The window is always drawn above the XAML content, which is acceptable for a dedicated
/// video area but means XAML cannot overlay it.
/// Create, move and destroy this on the thread that owns <c>parentHwnd</c> (the UI thread).
/// </remarks>
public sealed class MpvVideoSurface : IDisposable
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const int HwndTop = 0;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private nint _handle;
    private bool _disposed;
    private bool _visible;

    private MpvVideoSurface(nint handle)
    {
        _handle = handle;
    }

    /// <summary>Handle passed to mpv's <c>wid</c> option.</summary>
    public nint Handle => _handle;

    /// <summary>Creates the child window. The parent must be a real top-level window handle.</summary>
    public static MpvVideoSurface Create(nint parentHwnd)
    {
        if (parentHwnd == 0)
        {
            throw new ArgumentException("A valid parent window handle is required.", nameof(parentHwnd));
        }

        var handle = CreateWindowEx(
            0,
            "static",
            "DlnRendererVideo",
            WsChild | WsClipSiblings | WsClipChildren,
            0,
            0,
            1,
            1,
            parentHwnd,
            0,
            GetModuleHandle(null),
            0);

        if (handle == 0)
        {
            throw new InvalidOperationException(
                $"CreateWindowEx failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        // Hidden until a video track is actually present.
        ShowWindow(handle, SwHide);
        return new MpvVideoSurface(handle);
    }

    /// <summary>Moves and resizes the surface. Coordinates are physical pixels, relative to the parent client area.</summary>
    public void SetBounds(int x, int y, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle == 0)
        {
            return;
        }

        // Re-raise as well: WinUI's content bridge is a sibling child window and can end up on top.
        SetWindowPos(_handle, HwndTop, x, y, Math.Max(1, width), Math.Max(1, height), SwpNoActivate);
    }

    public void SetVisible(bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle == 0)
        {
            return;
        }

        if (_visible != visible)
        {
            _visible = visible;
            ShowWindow(_handle, visible ? SwShowNoActivate : SwHide);
        }

        if (visible)
        {
            // WinUI's DesktopChildSiteBridge is a sibling HWND; keep the video above it.
            SetWindowPos(_handle, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var handle = _handle;
        _handle = 0;

        if (handle != 0 && IsWindow(handle))
        {
            DestroyWindow(handle);
        }
    }

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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
