using System.Runtime.InteropServices;

namespace DlnRenderer.Service.Hosting;

/// <summary>
/// Places a window in the topmost band and verifies that Windows accepted it.
/// </summary>
/// <remarks>
/// Topmost is requested with <c>SetWindowPos(HWND_TOPMOST)</c> on a window that is already visible.
/// Doing it in that order matters: on a still-hidden window Windows accepts the call but does not
/// apply <c>WS_EX_TOPMOST</c>, leaving a visible overlay that ordinary windows can cover. The style
/// is read back afterwards because the platform can refuse it in some desktop compositions, and a
/// silent difference is exactly what must not happen here.
/// </remarks>
internal static class TopmostWindow
{
    private const long WsExTopmost = 0x00000008L;
    private const int GwlExStyle = -20;
    private const nint HwndTopmost = -1;
    private const nint HwndNotTopmost = -2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>True when the window currently carries <c>WS_EX_TOPMOST</c>.</summary>
    public static bool IsTopmost(nint window)
        => window != 0 && (GetWindowLongPtr(window, GwlExStyle).ToInt64() & WsExTopmost) != 0;

    /// <summary>
    /// Requests the topmost band for a visible window and reports whether it took effect.
    /// </summary>
    /// <param name="window">Top-level window handle.</param>
    /// <param name="activate">
    /// True when this process is currently in front and the window may take the foreground; false
    /// when it must not steal focus from whatever the user is using.
    /// </param>
    public static bool Apply(nint window, bool activate)
    {
        if (window == 0)
        {
            return false;
        }

        var flags = activate ? 0u : SwpNoActivate;

        // Placing a visible window in the topmost band is what sets the style.
        _ = SetWindowPos(window, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | flags);

        if (IsTopmost(window))
        {
            return true;
        }

        // Some shells only honour the style when it is set explicitly as well.
        var current = GetWindowLongPtr(window, GwlExStyle).ToInt64();
        _ = SetWindowLongPtr(window, GwlExStyle, (nint)(current | WsExTopmost));
        _ = SetWindowPos(window, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | flags);

        return IsTopmost(window);
    }

    /// <summary>Moves the window out of the topmost band and hides it; it stays alive for reuse.</summary>
    public static void Leave(nint window)
    {
        if (window == 0)
        {
            return;
        }

        _ = SetWindowPos(window, HwndNotTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

        var current = GetWindowLongPtr(window, GwlExStyle).ToInt64();
        if ((current & WsExTopmost) != 0)
        {
            _ = SetWindowLongPtr(window, GwlExStyle, (nint)(current & ~WsExTopmost));
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
}
