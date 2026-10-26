using System.Runtime.InteropServices;
using DlnRenderer.Core.Ipc;
using DlnRenderer.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace DlnRenderer;

/// <summary>
/// The transport bar that floats over a fullscreen cast.
/// </summary>
/// <remarks>
/// Why this is a window and not part of the page: the picture is a native child window of the main
/// window, and Windows paints child windows <em>above</em> their parent's XAML content. XAML can
/// therefore never be drawn over the video - but a top-level window owned by the main window can,
/// because an owned window is always above its owner.
/// <para>
/// It is only shown in fullscreen, where the video covers the page's dock: there it appears on any
/// mouse movement and hides itself after a couple of seconds without one, like any video player. A
/// windowed cast uses the dock below the stage instead, so the bar does not sit over a film that
/// still has its own controls visible. The pointer is sampled with <c>GetCursorPos</c> rather than
/// XAML pointer events: most of the window is the video, which belongs to the service process and
/// reports nothing to the UI.
/// </para>
/// </remarks>
public sealed partial class TransportOverlayWindow : Window
{
    /// <summary>Bar size in DIPs; widened for the volume slider, and clamped to the stage by layout.</summary>
    private const int BarWidthDip = 720;

    private const int BarHeightDip = 72;

    /// <summary>Gap between the bar and the bottom edge of the video it rides over, in DIPs.</summary>
    private const int BottomMarginDip = 20;

    /// <summary>Corner radius of the card, in DIPs; the window region is cut to match.</summary>
    private const int CornerRadiusDip = 16;

    /// <summary>How long the bar stays up after the last mouse movement.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(2.5);

    private const int GwlExStyle = -20;
    private const int GwlHwndParent = -8;
    private const long WsExToolWindow = 0x00000080;

    /// <summary>DWMWA_BORDER_COLOR, and the value that turns the frame off entirely.</summary>
    private const int DwmwaBorderColor = 34;

    private const uint DwmColorNone = 0xFFFFFFFE;

    /// <summary>DWMWA_NCRENDERING_POLICY and DWMNCRP_DISABLED.</summary>
    private const int DwmwaNcRenderingPolicy = 2;

    private const int DwmNcRenderingDisabled = 1;

    private readonly nint _handle;
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _suppressSlider;
    private bool _suppressVolume;
    private nint _owner;
    private PointInt32 _lastCursor;
    private DateTime _lastActivity = DateTime.UtcNow;
    private bool _shown;

    public TransportOverlayWindow()
    {
        InitializeComponent();

        _handle = WindowNative.GetWindowHandle(this);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = false;
        }

        // No taskbar entry and no Alt+Tab slot: it is part of the main window, not an application.
        var style = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
        _ = SetWindowLongPtr(_handle, GwlExStyle, (nint)(style | WsExToolWindow));

        // Windows 11 draws a thin frame around every window, including borderless ones. Around a
        // rounded bar it reads as a stray ring, so it is removed explicitly.
        RemoveWindowFrame();

        AppWindow.Title = "DlnRenderer 播放控制";

        _idleTimer.Tick += OnIdleTick;

        App.Client.SnapshotChanged += OnSnapshotChanged;
        Closed += (_, _) =>
        {
            _idleTimer.Stop();
            App.Client.SnapshotChanged -= OnSnapshotChanged;
        };
    }

    /// <summary>
    /// Shows the bar over <paramref name="owner"/>'s bottom edge, or keeps it in front.
    /// </summary>
    public void ShowOver(MainWindow owner)
    {
        // Ownership keeps the bar above the video and lets it disappear with the window. It can only
        // be set once, before the first show, so it is skipped when it is already correct.
        var ownerHandle = WindowNative.GetWindowHandle(owner);
        if (_owner != ownerHandle)
        {
            _ = SetWindowLongPtr(_handle, GwlHwndParent, ownerHandle);
            _owner = ownerHandle;
        }

        ApplyLayout(owner);
        AppWindow.Show();
        _shown = true;
        Touch();

        // Re-applied after the window is visible: WinUI refreshes the window's frame attributes when
        // it shows the window, which quietly undoes the frame removal done in the constructor. The
        // leftover frame is the pale ring that was visible around the bar.
        RemoveWindowFrame();

        if (!_idleTimer.IsEnabled)
        {
            _idleTimer.Start();
        }

        ApplySnapshot(App.Client.Snapshot);
    }

    /// <summary>Moves the bar to follow the window it belongs to.</summary>
    public void FollowOwner(MainWindow owner)
    {
        if (_owner == 0 || !_shown)
        {
            return;
        }

        ApplyLayout(owner);
    }

    /// <summary>Hides the bar for good, e.g. when the page goes away.</summary>
    public void HideOverlay()
    {
        _idleTimer.Stop();
        _shown = false;
        AppWindow.Hide();
    }

    /// <summary>
    /// Removes the system-drawn frame and non-client rendering around the bar.
    /// </summary>
    /// <remarks>
    /// Two attributes, because they fail independently: the border colour is honoured by Windows 11
    /// only, and disabling non-client rendering covers the shadow and the resize frame on builds that
    /// ignore the colour. Failures are logged rather than thrown - a stray ring is cosmetic.
    /// </remarks>
    private void RemoveWindowFrame()
    {
        var noBorder = DwmColorNone;
        var border = DwmSetWindowAttribute(_handle, DwmwaBorderColor, ref noBorder, sizeof(int));
        if (border < 0)
        {
            DiagnosticsLog.Write($"overlay: DWMWA_BORDER_COLOR failed (0x{border:X8})");
        }

        var noNonClient = (uint)DwmNcRenderingDisabled;
        var rendering = DwmSetWindowAttribute(_handle, DwmwaNcRenderingPolicy, ref noNonClient, sizeof(uint));
        if (rendering < 0)
        {
            DiagnosticsLog.Write($"overlay: DWMWA_NCRENDERING_POLICY failed (0x{rendering:X8})");
        }
    }

    /// <summary>
    /// Places the bar at the bottom edge of the video area when one is known, else at the window's
    /// bottom edge, and keeps it in front.
    /// </summary>
    /// <remarks>
    /// The stage rectangle is what the page reserved for the picture; anchoring the bar to it keeps
    /// the controls over the film instead of over the page's own dock, which shows the same commands.
    /// </remarks>
    private void ApplyLayout(MainWindow owner)
    {
        var scale = owner.Content?.XamlRoot?.RasterizationScale ?? 1.0;
        var height = (int)Math.Round(BarHeightDip * scale);

        var stage = owner.StageScreenRect;
        var windowBounds = owner.AppWindow.Position;
        var windowSize = owner.AppWindow.Size;

        int width;
        int x;
        int y;

        if (stage.Width > 1 && stage.Height > 1)
        {
            width = Math.Min((int)Math.Round(BarWidthDip * scale), Math.Max(200, stage.Width - (int)Math.Round(32 * scale)));
            x = stage.X + ((stage.Width - width) / 2);
            y = stage.Y + stage.Height - height - (int)Math.Round(BottomMarginDip * scale);

            // Defensive: a stale stage rectangle from before a window move could sit off-window for
            // one frame until the page re-reports. Clamping keeps the bar reachable.
            x = Math.Clamp(x, windowBounds.X, windowBounds.X + Math.Max(0, windowSize.Width - width));
            y = Math.Clamp(y, windowBounds.Y, windowBounds.Y + Math.Max(0, windowSize.Height - height));
        }
        else
        {
            width = Math.Min((int)Math.Round(BarWidthDip * scale), Math.Max(200, windowSize.Width - 80));
            x = windowBounds.X + ((windowSize.Width - width) / 2);
            y = windowBounds.Y + windowSize.Height - height - (int)Math.Round(BottomMarginDip * scale);
        }

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));

        // Clip the window itself to the card's rounded shape. Without this the rounded border would
        // sit on a rectangular slab of window background, which is what made the bar look broken
        // over video. A window region cuts the corners for real: nothing is painted there at all.
        // The region covers the client area exactly - a region even one pixel larger leaves a sliver
        // of window background visible around the edge.
        var radius = (int)Math.Round(CornerRadiusDip * scale);
        var region = CreateRoundRectRgn(0, 0, width, height, radius * 2, radius * 2);
        if (region != 0)
        {
            // The system owns the region once it is set, so it must not be deleted here.
            _ = SetWindowRgn(_handle, region, redraw: true);
        }
    }

    /// <summary>Resets the idle countdown, so the bar stays up while the user is doing something.</summary>
    private void Touch() => _lastActivity = DateTime.UtcNow;

    private void OnIdleTick(object? sender, object e)
    {
        var owner = App.MainWindow;
        if (owner is null || !_shown)
        {
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        var moved = cursor.X != _lastCursor.X || cursor.Y != _lastCursor.Y;
        _lastCursor = cursor;

        var window = owner.AppWindow.Position;
        var size = owner.AppWindow.Size;
        var insideWindow = cursor.X >= window.X && cursor.X < window.X + size.Width
            && cursor.Y >= window.Y && cursor.Y < window.Y + size.Height;

        // The bar's own rectangle counts as activity: it is where the user clicks and drags.
        var bar = AppWindow.Position;
        var barSize = AppWindow.Size;
        var overBar = cursor.X >= bar.X && cursor.X < bar.X + barSize.Width
            && cursor.Y >= bar.Y && cursor.Y < bar.Y + barSize.Height;

        if (overBar || (insideWindow && moved))
        {
            Touch();

            if (!AppWindow.IsVisible)
            {
                // Show without activation: the bar reports state, it is not a window to work in.
                AppWindow.Show(activateWindow: false);
                ApplyLayout(owner);
            }

            return;
        }

        if (DateTime.UtcNow - _lastActivity > IdleTimeout)
        {
            AppWindow.Hide();
        }
    }

    private void RevealOwner(MainWindow owner)
    {
        if (!_shown)
        {
            return;
        }

        Touch();

        if (!AppWindow.IsVisible)
        {
            // Show without activation: the bar is a viewer of state, not a window the user works in.
            AppWindow.Show(activateWindow: false);
            ApplyLayout(owner);
        }
    }

    // ---- transport ---------------------------------------------------------------------------

    private async void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        var playing = App.Client.Snapshot?.Playback.State == PlaybackState.Playing;
        await SendAsync(playing ? IpcCommands.Pause : IpcCommands.Play);
        Touch();
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        Touch();
        await SendAsync(IpcCommands.Stop);
    }

    private async void OnMuteClick(object sender, RoutedEventArgs e)
    {
        Touch();
        var muted = App.Client.Snapshot?.Playback.Muted ?? false;
        try
        {
            await App.Client.SendAsync(IpcCommands.SetMute, muted: !muted);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"overlay mute failed: {ex.Message}");
        }
    }

    private async void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        Touch();

        if (_suppressVolume)
        {
            return;
        }

        try
        {
            await App.Client.SendAsync(IpcCommands.SetVolume, volume: (int)Math.Round(e.NewValue));
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"overlay volume failed: {ex.Message}");
        }
    }

    private async void OnPositionChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        Touch();

        if (_suppressSlider)
        {
            return;
        }

        var seconds = e.NewValue;
        var current = App.Client.Snapshot?.Playback.Position.TotalSeconds ?? 0;

        // Snapshots tick constantly; ignore the sub-second drift they cause, the same way the page
        // does, so a poll never looks like a user drag.
        if (Math.Abs(seconds - current) < 1.0)
        {
            return;
        }

        try
        {
            await App.Client.SendAsync(IpcCommands.Seek, seekSeconds: seconds);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"overlay seek failed: {ex.Message}");
        }
    }

    private void OnFullScreenClick(object sender, RoutedEventArgs e)
    {
        Touch();

        var owner = App.MainWindow;
        if (owner is null)
        {
            return;
        }

        owner.SetFullScreen(!owner.IsFullScreen);
        FollowOwner(owner);

        // The glyph reflects which way the button now goes; no snapshot arrives for this change.
        ApplySnapshot(App.Client.Snapshot);
    }

    private async Task SendAsync(string command)
    {
        try
        {
            await App.Client.SendAsync(command);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"overlay command '{command}' failed: {ex.Message}");
        }
    }

    // ---- state -------------------------------------------------------------------------------

    private void OnSnapshotChanged(object? sender, RendererSnapshot snapshot)
        => DispatcherQueue.TryEnqueue(() =>
        {
            ApplySnapshot(snapshot);

            // A state change means the user just acted somewhere (phone or page): show the bar
            // again rather than leave them guessing where the controls went.
            if (App.MainWindow is { } owner)
            {
                RevealOwner(owner);
            }
        });

    private void ApplySnapshot(RendererSnapshot snapshot)
    {
        var status = snapshot.Playback;
        var playing = status.State == PlaybackState.Playing;

        PlayPauseButton.Content = new FontIcon { FontSize = 14, Glyph = playing ? "\uE769" : "\uE768" };
        MuteButton.Content = new FontIcon { FontSize = 14, Glyph = status.Muted ? "\uE74F" : "\uE767" };
        FullScreenButton.Content = new FontIcon
        {
            FontSize = 14,
            Glyph = App.MainWindow?.IsFullScreen == true ? "\uE73F" : "\uE740",
        };

        ElapsedText.Text = Format(status.Position);
        TotalText.Text = Format(status.Duration ?? TimeSpan.Zero);

        // Do not fight the user's thumb: while dragging, the slider is theirs.
        _suppressSlider = true;
        PositionSlider.Maximum = Math.Max(1, (status.Duration ?? TimeSpan.Zero).TotalSeconds);
        PositionSlider.Value = status.Position.TotalSeconds;
        _suppressSlider = false;

        _suppressVolume = true;
        VolumeSlider.Value = status.Volume;
        _suppressVolume = false;

        // The service names its kernel; an old service sends nothing and the badge stays hidden
        // rather than pretending to know.
        if (snapshot.Engine is { } engineId)
        {
            EngineBadge.Text = PlaybackEngineInfo.DisplayName(PlaybackEngineInfo.FromId(engineId));
            EngineBadge.Visibility = Visibility.Visible;
        }
        else
        {
            EngineBadge.Visibility = Visibility.Collapsed;
        }
    }

    private static string Format(TimeSpan value)
        => value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";

    [StructLayout(LayoutKind.Sequential)]
    private struct PointInt32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointInt32 point);

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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
