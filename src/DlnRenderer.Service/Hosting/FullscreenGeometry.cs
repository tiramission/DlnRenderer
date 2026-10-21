using System;

namespace DlnRenderer.Service.Hosting;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct ScreenBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public override string ToString() => $"{Left},{Top} {Width}x{Height}";
}

/// <summary>One monitor as reported by Windows.</summary>
/// <param name="Monitor">Native monitor handle, used to verify a selection is still valid.</param>
/// <param name="DeviceName">Stable device name such as <c>\\.\DISPLAY1</c>.</param>
/// <param name="MonitorBounds">Full monitor rectangle (the whole screen).</param>
/// <param name="WorkArea">Monitor rectangle minus the taskbar.</param>
/// <param name="IsPrimary">True for the primary monitor.</param>
public readonly record struct MonitorInfo(
    nint Monitor,
    string DeviceName,
    ScreenBounds MonitorBounds,
    ScreenBounds WorkArea,
    bool IsPrimary);

/// <summary>
/// Computes the window rectangle for borderless fullscreen.
/// </summary>
/// <remarks>
/// Fullscreen covers the monitor rectangle, not a hardcoded 1920x1080 and not the work area: a
/// renderer that leaves the taskbar strip unpainted is not fullscreen, and one that assumes a
/// resolution breaks on every other display and on resolution changes.
/// </remarks>
public static class FullscreenGeometry
{
    /// <summary>Chooses the monitor to cover.</summary>
    /// <param name="monitors">All monitors attached to the desktop.</param>
    /// <param name="preferred">A monitor the caller wants to keep, if it is still attached.</param>
    /// <param name="anchor">Window rectangle used to pick a monitor when none is preferred.</param>
    /// <remarks>
    /// Reusing <paramref name="preferred"/> keeps a window on the display it is already on across
    /// play/pause/display changes. When it is gone - unplugged, or the display was reconfigured -
    /// the selection falls back to the monitor the window currently occupies, then to the primary
    /// one. It never silently jumps back to coordinates 0,0 on a multi-monitor desktop.
    /// </remarks>
    public static MonitorInfo SelectMonitor(
        ReadOnlySpan<MonitorInfo> monitors,
        MonitorInfo? preferred,
        ScreenBounds anchor)
    {
        if (monitors.IsEmpty)
        {
            throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        }

        if (preferred is { } kept && ContainsMonitor(monitors, kept.Monitor))
        {
            return kept;
        }

        // The window's current monitor: the one holding its centre point.
        var centreX = anchor.Left + (anchor.Width / 2);
        var centreY = anchor.Top + (anchor.Height / 2);
        foreach (var monitor in monitors)
        {
            if (monitor.MonitorBounds.Left <= centreX && centreX < monitor.MonitorBounds.Right
                && monitor.MonitorBounds.Top <= centreY && centreY < monitor.MonitorBounds.Bottom)
            {
                return monitor;
            }
        }

        foreach (var monitor in monitors)
        {
            if (monitor.IsPrimary)
            {
                return monitor;
            }
        }

        return monitors[0];
    }

    /// <summary>Rectangle that covers <paramref name="monitor"/> entirely.</summary>
    public static ScreenBounds FullscreenFor(MonitorInfo monitor)
    {
        var bounds = monitor.MonitorBounds;
        if (bounds.IsEmpty)
        {
            throw new ArgumentException("The selected monitor has an empty rectangle.", nameof(monitor));
        }

        return bounds;
    }

    private static bool ContainsMonitor(ReadOnlySpan<MonitorInfo> monitors, nint handle)
    {
        foreach (var monitor in monitors)
        {
            if (monitor.Monitor == handle)
            {
                return true;
            }
        }

        return false;
    }
}
