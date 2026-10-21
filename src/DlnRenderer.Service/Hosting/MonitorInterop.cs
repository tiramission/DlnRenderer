using System.Runtime.InteropServices;

namespace DlnRenderer.Service.Hosting;

/// <summary>Enumerates the physical monitors attached to the desktop.</summary>
/// <remarks>
/// The service is DPI aware through its application manifest (see
/// <c>DlnRenderer.Service.csproj</c>), so the rectangles returned here are physical pixels and can
/// be handed to <c>SetWindowPos</c> unchanged.
/// <para>
/// Manifest rather than a runtime call: by the time <c>Main</c> runs, .NET startup has already
/// created windows on a thread, and Windows refuses to change awareness afterwards. A
/// non-aware process at 150% scaling sees a 2560x1440 display as 1707x960, and a fullscreen
/// window sized from that covers only two thirds of the screen.
/// </para>
/// </remarks>
public static class MonitorInterop
{
    private const uint MonitorInfoPrimary = 0x00000001;


    /// <summary>Returns every monitor currently attached, in physical pixels.</summary>
    public static MonitorInfo[] GetMonitors()
    {
        var monitors = new List<MonitorInfo>();

        _ = EnumDisplayMonitors(0, 0, (handle, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(handle, ref info))
            {
                return true;
            }

            monitors.Add(new MonitorInfo(
                handle,
                info.DeviceName ?? string.Empty,
                ToBounds(info.Monitor),
                ToBounds(info.WorkArea),
                (info.Flags & MonitorInfoPrimary) != 0));

            return true;
        }, 0);

        // A desktop always has at least one monitor; an empty list means Win32 enumeration failed.
        // Falling back to the virtual screen keeps the renderer usable instead of throwing during
        // playback. Selection treats this as a single display covering the whole desktop.
        if (monitors.Count == 0)
        {
            var width = GetSystemMetrics(SystemMetricVirtualScreenX);
            var height = GetSystemMetrics(SystemMetricVirtualScreenY);
            monitors.Add(new MonitorInfo(
                0,
                "<virtual screen>",
                new ScreenBounds(0, 0, Math.Max(1, width), Math.Max(1, height)),
                new ScreenBounds(0, 0, Math.Max(1, width), Math.Max(1, height)),
                IsPrimary: true));
        }

        return [.. monitors];
    }

    /// <summary>
    /// Monitor handle for a window's current rectangle, or zero when it cannot be determined.
    /// </summary>
    public static nint MonitorFromWindow(nint window)
        => window == 0 ? 0 : MonitorFromWindow(window, MonitorDefaultToNearest);

    private static ScreenBounds ToBounds(NativeRect rect)
        => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private const int SystemMetricVirtualScreenX = 78;
    private const int SystemMetricVirtualScreenY = 79;
    private const uint MonitorDefaultToNearest = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? DeviceName;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool MonitorEnumProcedure(nint monitor, nint deviceContext, nint rect, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRect,
        MonitorEnumProcedure procedure,
        nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
