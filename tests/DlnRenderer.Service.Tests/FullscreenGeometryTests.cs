using DlnRenderer.Service.Hosting;

namespace DlnRenderer.Service.Tests;

/// <summary>Fullscreen geometry and monitor selection, independent of any real display.</summary>
public class FullscreenGeometryTests
{
    private static MonitorInfo Monitor(
        nint handle,
        int left,
        int top,
        int width,
        int height,
        bool primary = false)
        => new(
            handle,
            $"\\\\.\\DISPLAY{handle}",
            new ScreenBounds(left, top, left + width, top + height),
            // Work area deliberately smaller, as on a desktop with a taskbar.
            new ScreenBounds(left, top, left + width, top + height - 48),
            primary);

    [Fact]
    public void Fullscreen_covers_the_whole_monitor_not_the_work_area()
    {
        var monitor = Monitor(1, 0, 0, 2560, 1440, primary: true);

        var bounds = FullscreenGeometry.FullscreenFor(monitor);

        Assert.Equal(new ScreenBounds(0, 0, 2560, 1440), bounds);
        Assert.NotEqual(monitor.WorkArea, bounds);
    }

    [Fact]
    public void Fullscreen_uses_the_monitors_physical_origin_on_a_second_display()
    {
        // A second display sits to the left of the primary one, so its origin is negative.
        var monitor = Monitor(2, -1920, -120, 1920, 1080);

        var bounds = FullscreenGeometry.FullscreenFor(monitor);

        // The classic multi-monitor bug is forcing 0,0 when the display is not there.
        Assert.Equal(-1920, bounds.Left);
        Assert.Equal(-120, bounds.Top);
        Assert.Equal(1920, bounds.Width);
        Assert.Equal(1080, bounds.Height);
    }

    [Fact]
    public void Preferred_monitor_is_kept_across_play_pause_and_display_changes()
    {
        MonitorInfo[] monitors =
        [
            Monitor(1, 0, 0, 1920, 1080, primary: true),
            Monitor(2, 1920, 0, 2560, 1440),
        ];

        var selected = FullscreenGeometry.SelectMonitor(monitors, monitors[1], anchor: default);

        Assert.Equal(monitors[1].Monitor, selected.Monitor);
    }

    [Fact]
    public void Unplugged_monitor_falls_back_to_the_primary_when_the_window_is_off_every_display()
    {
        MonitorInfo[] monitors =
        [
            Monitor(1, 0, 0, 1920, 1080, primary: true),
            Monitor(2, 1920, 0, 2560, 1440),
        ];

        // Handle 3 was unplugged, and the window's old position (4000..4800) is not on any remaining
        // display. Selection must still land on a real monitor, not on the stale coordinates.
        var gone = Monitor(3, 4000, 0, 800, 600);
        var selected = FullscreenGeometry.SelectMonitor(monitors, gone, new ScreenBounds(5000, 0, 5800, 600));

        Assert.Equal(monitors[0].Monitor, selected.Monitor);
        Assert.Equal(new ScreenBounds(0, 0, 1920, 1080), FullscreenGeometry.FullscreenFor(selected));
    }

    [Fact]
    public void Unplugged_monitor_falls_back_to_the_display_the_window_now_overlaps()
    {
        // Same monitors, but the window sits on the remaining secondary display: that is the one to
        // cover, even though the primary has priority when nothing matches.
        MonitorInfo[] monitors =
        [
            Monitor(1, 0, 0, 1920, 1080, primary: true),
            Monitor(2, 1920, 0, 2560, 1440),
        ];

        var gone = Monitor(3, 5000, 0, 800, 600);
        var selected = FullscreenGeometry.SelectMonitor(monitors, gone, new ScreenBounds(2000, 100, 2600, 700));

        Assert.Equal(monitors[1].Monitor, selected.Monitor);
    }

    [Fact]
    public void Window_on_a_secondary_display_selects_that_display()
    {
        MonitorInfo[] monitors =
        [
            Monitor(1, 0, 0, 1920, 1080, primary: true),
            Monitor(2, 1920, 0, 2560, 1440),
        ];

        var selected = FullscreenGeometry.SelectMonitor(
            monitors,
            preferred: null,
            anchor: new ScreenBounds(2000, 100, 2600, 700));

        Assert.Equal(monitors[1].Monitor, selected.Monitor);
    }

    [Fact]
    public void Without_a_window_anchor_the_primary_monitor_is_used()
    {
        MonitorInfo[] monitors =
        [
            Monitor(2, 1920, 0, 2560, 1440),
            Monitor(1, 0, 0, 1920, 1080, primary: true),
        ];

        // Anchor outside every display (the window was never positioned).
        var selected = FullscreenGeometry.SelectMonitor(monitors, null, new ScreenBounds(-9999, -9999, 1, 1));

        Assert.Equal(monitors[1].Monitor, selected.Monitor);
    }

    [Fact]
    public void Resolution_change_is_reflected_because_the_rectangle_is_recomputed()
    {
        var before = Monitor(1, 0, 0, 1920, 1080, primary: true);
        var after = Monitor(1, 0, 0, 2560, 1440, primary: true);

        Assert.NotEqual(FullscreenGeometry.FullscreenFor(before), FullscreenGeometry.FullscreenFor(after));
        Assert.Equal(new ScreenBounds(0, 0, 2560, 1440), FullscreenGeometry.FullscreenFor(after));
    }

    [Fact]
    public void Selection_requires_at_least_one_monitor()
    {
        Assert.Throws<ArgumentException>(() =>
            FullscreenGeometry.SelectMonitor(ReadOnlySpan<MonitorInfo>.Empty, null, default));
    }

    [Fact]
    public void Empty_monitor_rectangle_is_rejected_rather_than_forced_to_one_pixel()
    {
        var degenerate = new MonitorInfo(1, "DISPLAY1", new ScreenBounds(0, 0, 0, 0), new ScreenBounds(0, 0, 1, 1), true);

        Assert.Throws<ArgumentException>(() => FullscreenGeometry.FullscreenFor(degenerate));
    }

    [Fact]
    public void Real_desktop_reports_at_least_one_usable_monitor()
    {
        // Guards MonitorInterop's Win32 marshalling; runs on a headless agent too because a
        // virtual-screen fallback is supplied when enumeration returns nothing.
        var monitors = MonitorInterop.GetMonitors();

        Assert.NotEmpty(monitors);
        Assert.Contains(monitors, m => m.MonitorBounds.Width > 0 && m.MonitorBounds.Height > 0);
    }
}
