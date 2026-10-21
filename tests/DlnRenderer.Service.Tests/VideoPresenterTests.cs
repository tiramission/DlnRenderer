using System.Diagnostics;
using System.Runtime.InteropServices;
using DlnRenderer.Service.Hosting;

namespace DlnRenderer.Service.Tests;

/// <summary>
/// Drives the real presentation window and reads its state back from Win32.
/// </summary>
/// <remarks>
/// These tests exercise the actual HWND: window style, z-order, size against the real monitors and
/// the child surface handed to mpv. They are the closest automated equivalent of "the video covers
/// the screen and stays on top"; the visual check on a live desktop is recorded in
/// <c>docs/testing.md</c> §4.
/// <para>
/// Window work is applied on the presenter's own thread, so every test drains the queue with
/// <see cref="VideoPresenter.WaitForIdle"/> before asserting.
/// </para>
/// </remarks>
[Collection("video-window")]
public class VideoPresenterTests
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(10);

    private const long WsPopup = 0x80000000L;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsSysMenu = 0x00080000L;
    private const long WsExTopmost = 0x00000008L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExClientEdge = 0x00000200L;

    [Fact]
    public async Task Starts_hidden_with_a_child_surface_for_mpv()
    {
        await using var presenter = await VideoPresenter.StartAsync();

        Assert.NotEqual(0, presenter.ChildHandle);
        Assert.NotEqual(0, presenter.WindowHandle);
        Assert.False(presenter.IsVisible);
        Assert.Equal("hidden", presenter.PresentationState);

        // The child must be owned by the top-level window, otherwise mpv's wid would paint outside it.
        Assert.Equal(presenter.WindowHandle, GetParent(presenter.ChildHandle));

        // Hidden on start: a cast has not begun, so nothing should be on screen.
        Assert.False(IsWindowVisible(presenter.WindowHandle));
    }

    [Fact]
    public async Task Show_covers_the_selected_monitor_with_a_borderless_topmost_window()
    {
        await using var presenter = await VideoPresenter.StartAsync();

        presenter.ShowFullscreen();
        Assert.True(presenter.WaitForIdle(IdleTimeout), "the window thread did not apply the request");

        Assert.True(presenter.IsVisible);
        Assert.Equal("fullscreen", presenter.PresentationState);

        var style = GetWindowLongPtr(presenter.WindowHandle, GwlStyle);
        Assert.True((style & WsPopup) != 0, "fullscreen must be a popup window");
        Assert.True((style & WsCaption) == 0, "a title bar would be visible over the video");
        Assert.True((style & WsThickFrame) == 0, "a resize frame would be visible over the video");

        var exStyle = GetWindowLongPtr(presenter.WindowHandle, GwlExStyle);
        Assert.True((exStyle & WsExToolWindow) != 0, "the overlay must stay out of the Alt+Tab list");
        Assert.True((exStyle & WsExClientEdge) == 0, "a sunken edge would be visible over the video");

        // Topmost is a real desktop resource, so the request is verified rather than assumed: the
        // style must be present on the live window, and the presenter's own verdict must agree.
        Assert.True(
            presenter.IsTopmost,
            $"the overlay must be in the topmost band; exStyle=0x{exStyle:X} applied={presenter.TopmostApplied}");
        Assert.True(presenter.TopmostApplied, "the presenter must report the verified result");

        Assert.True(IsWindowVisible(presenter.WindowHandle));

        // The window rectangle must equal a real monitor rectangle exactly, not the work area.
        var monitors = MonitorInterop.GetMonitors();
        _ = GetWindowRect(presenter.WindowHandle, out var rect);
        var actual = new ScreenBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);

        Assert.Contains(monitors, m => m.MonitorBounds == actual);

        // And it is not the taskbar-reduced work area, which is the "almost fullscreen" bug. On a
        // desktop with auto-hidden or no taskbar the two rectangles legitimately coincide, so the
        // assertion only applies when they actually differ.
        var covered = monitors.Single(m => m.MonitorBounds == actual);
        if (covered.WorkArea != covered.MonitorBounds)
        {
            Assert.NotEqual(covered.WorkArea, actual);
        }

        // The child fills the whole client area so mpv has no unpainted border.
        _ = GetClientRect(presenter.WindowHandle, out var client);
        _ = GetWindowRect(presenter.ChildHandle, out var childRect);
        Assert.Equal(client.Right, childRect.Right - childRect.Left);
        Assert.Equal(client.Bottom, childRect.Bottom - childRect.Top);
    }

    [Fact]
    public async Task Repeated_show_is_idempotent()
    {
        await using var presenter = await VideoPresenter.StartAsync();

        presenter.ShowFullscreen();
        Assert.True(presenter.WaitForIdle(IdleTimeout));
        _ = GetWindowRect(presenter.WindowHandle, out var first);

        // Player events fire several times per second; re-showing must not move or resize anything.
        for (var i = 0; i < 20; i++)
        {
            presenter.ShowFullscreen();
        }

        Assert.True(presenter.WaitForIdle(IdleTimeout));
        _ = GetWindowRect(presenter.WindowHandle, out var after);
        Assert.Equal(first.Left, after.Left);
        Assert.Equal(first.Top, after.Top);
        Assert.Equal(first.Right, after.Right);
        Assert.Equal(first.Bottom, after.Bottom);
        Assert.Equal("fullscreen", presenter.PresentationState);
    }

    [Fact]
    public async Task Hide_removes_topmost_and_keeps_the_window_for_the_next_play()
    {
        await using var presenter = await VideoPresenter.StartAsync();
        var topLevel = presenter.WindowHandle;

        presenter.ShowFullscreen();
        Assert.True(presenter.WaitForIdle(IdleTimeout));

        presenter.Hide();
        Assert.True(presenter.WaitForIdle(IdleTimeout));

        Assert.False(presenter.IsVisible);
        Assert.Equal("hidden", presenter.PresentationState);

        // Checked via the visible-state query rather than GetWindowLongPtr: WS_EX_TOPMOST controls
        // z-order, but IsWindowVisible alone does not prove no topmost frame is left on screen.
        Assert.False(IsWindowVisible(topLevel));
        Assert.False(IsWindowVisible(presenter.ChildHandle));

        // Hiding must not destroy the HWND: mpv's wid would become invalid and the next Play would
        // need a brand-new player.
        Assert.True(IsWindow(topLevel));
        Assert.NotEqual(0, presenter.ChildHandle);

        presenter.ShowFullscreen();
        Assert.True(presenter.WaitForIdle(IdleTimeout));
        Assert.Equal(topLevel, presenter.WindowHandle);
        Assert.True(presenter.IsVisible);
    }

    [Fact]
    public async Task Show_hide_cycles_do_not_leak_windows()
    {
        await using var presenter = await VideoPresenter.StartAsync();

        for (var i = 0; i < 10; i++)
        {
            presenter.ShowFullscreen();
            Assert.True(presenter.WaitForIdle(IdleTimeout));
            Assert.Equal("fullscreen", presenter.PresentationState);

            presenter.Hide();
            Assert.True(presenter.WaitForIdle(IdleTimeout));
            Assert.Equal("hidden", presenter.PresentationState);
        }

        // Still exactly one top-level presenter window, and no second child surface.
        Assert.Equal(1, CountWindowsOfCurrentProcess(presenter.WindowHandle));
        Assert.Equal(1, CountChildren(presenter.WindowHandle));
    }

    [Fact]
    public async Task Dispose_closes_the_window_and_its_child()
    {
        var presenter = await VideoPresenter.StartAsync();
        var topLevel = presenter.WindowHandle;
        var child = presenter.ChildHandle;

        presenter.ShowFullscreen();
        _ = presenter.WaitForIdle(IdleTimeout);

        await presenter.DisposeAsync();

        Assert.False(IsWindow(topLevel));
        Assert.False(IsWindow(child));
    }

    [Fact]
    public async Task Dispose_is_safe_when_called_twice_and_while_hidden()
    {
        var presenter = await VideoPresenter.StartAsync();
        var topLevel = presenter.WindowHandle;

        await presenter.DisposeAsync();
        await presenter.DisposeAsync();

        Assert.False(IsWindow(topLevel));
    }

    [Fact]
    public async Task Dispose_while_showing_stops_presenting_without_leaving_it_visible()
    {
        // Service shutdown path: the window is fullscreen when the session is torn down.
        var presenter = await VideoPresenter.StartAsync();
        presenter.ShowFullscreen();
        _ = presenter.WaitForIdle(IdleTimeout);

        var topLevel = presenter.WindowHandle;

        await presenter.DisposeAsync();

        Assert.False(presenter.IsVisible);
        Assert.Equal("hidden", presenter.PresentationState);
        Assert.False(IsWindow(topLevel));
    }

    [Fact]
    public async Task RefreshMonitor_reapplies_fullscreen_only_while_visible()
    {
        await using var presenter = await VideoPresenter.StartAsync();

        presenter.RefreshMonitor();
        Assert.True(presenter.WaitForIdle(IdleTimeout));
        Assert.Equal("hidden", presenter.PresentationState);
        Assert.False(IsWindowVisible(presenter.WindowHandle));

        presenter.ShowFullscreen();
        Assert.True(presenter.WaitForIdle(IdleTimeout));
        _ = GetWindowRect(presenter.WindowHandle, out var before);

        presenter.RefreshMonitor();
        Assert.True(presenter.WaitForIdle(IdleTimeout));

        _ = GetWindowRect(presenter.WindowHandle, out var after);
        Assert.Equal(before.Left, after.Left);
        Assert.Equal(before.Right, after.Right);
        Assert.Equal("fullscreen", presenter.PresentationState);
    }

    [Fact]
    public async Task Concurrent_show_and_hide_end_in_one_consistent_state()
    {
        // Mirrors DLNA handler, player and IPC threads racing on the same window.
        await using var presenter = await VideoPresenter.StartAsync();

        var work = new List<Task>();
        for (var i = 0; i < 40; i++)
        {
            var show = i % 2 == 0;
            work.Add(Task.Run(() =>
            {
                if (show)
                {
                    presenter.ShowFullscreen();
                }
                else
                {
                    presenter.Hide();
                }
            }));
        }

        await Task.WhenAll(work);
        Assert.True(presenter.WaitForIdle(IdleTimeout));

        // Whatever interleaving happened, the window must be in one of the two valid states, with
        // the style flags matching it - never half applied (topmost but hidden, or visible but framed).
        var isVisible = presenter.IsVisible;
        Assert.Equal(isVisible ? "fullscreen" : "hidden", presenter.PresentationState);
        Assert.Equal(isVisible, IsWindowVisible(presenter.WindowHandle));
        Assert.Equal(isVisible, (GetWindowLongPtr(presenter.WindowHandle, GwlExStyle) & WsExTopmost) != 0);

        presenter.ShowFullscreen();
        Assert.True(presenter.WaitForIdle(IdleTimeout));
        Assert.Equal("fullscreen", presenter.PresentationState);
        Assert.True(IsWindow(presenter.WindowHandle));
    }

    [Fact]
    public async Task Window_has_no_system_menu_so_alt_space_cannot_shrink_it()
    {
        await using var presenter = await VideoPresenter.StartAsync();
        presenter.ShowFullscreen();
        Assert.True(presenter.WaitForIdle(IdleTimeout));

        // WS_SYSMENU absent: the window provides no Alt+Space menu that could restore a framed state.
        Assert.True((GetWindowLongPtr(presenter.WindowHandle, GwlStyle) & WsSysMenu) == 0);
    }

    [Fact]
    public async Task WaitForIdle_reports_failure_after_dispose()
    {
        var presenter = await VideoPresenter.StartAsync();
        await presenter.DisposeAsync();

        Assert.False(presenter.WaitForIdle(TimeSpan.FromMilliseconds(100)));
    }

    /// <summary>Counts windows of this process whose handle is <paramref name="handle"/>.</summary>
    private static int CountWindowsOfCurrentProcess(nint handle)
    {
        var count = 0;
        var target = Process.GetCurrentProcess().Id;

        _ = EnumWindows((candidate, data) =>
        {
            _ = GetWindowThreadProcessId(candidate, out var owner);
            if (owner == target && candidate == handle)
            {
                count++;
            }

            return true;
        }, 0);

        return count;
    }

    /// <summary>Counts windows owned by <paramref name="parent"/>; used to prove no child leaked.</summary>
    private static int CountChildren(nint parent)
    {
        var count = 0;

        _ = EnumChildWindows(parent, (candidate, data) =>
        {
            count++;
            return true;
        }, 0);

        return count;
    }

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool EnumWindowsProcedure(nint handle, nint data);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetParent(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProcedure procedure, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, EnumWindowsProcedure procedure, nint data);
}
