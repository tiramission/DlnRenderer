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
/// The transport bar that floats over an embedded cast.
/// </summary>
/// <remarks>
/// Why this is a window and not part of the page: in "UI window" mode the picture is a native child
/// window of the main window, and Windows paints child windows <em>above</em> their parent's XAML
/// content. XAML can therefore never be drawn over the video - but a top-level window owned by the
/// main window can, because an owned window is always above its owner.
/// <para>
/// It hides itself after a couple of seconds without mouse movement and comes back on the next
/// move, like any video player, so it does not sit on top of a film. The pointer is sampled with
/// <c>GetCursorPos</c> rather than XAML pointer events: most of the window is the video, which
/// belongs to the service process and reports nothing to the UI.
/// </para>
/// </remarks>
public sealed partial class TransportOverlayWindow : Window
{
    /// <summary>Bar size in DIPs; small enough to sit over a film without hiding much.</summary>
    private const int BarWidthDip = 640;

    private const int BarHeightDip = 72;

    private const int BottomMarginDip = 28;

    /// <summary>Corner radius of the card, in DIPs; the window region is cut to match.</summary>
    private const int CornerRadiusDip = 16;

    /// <summary>How long the bar stays up after the last mouse movement.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(2.5);

    private const int GwlExStyle = -20;
    private const int GwlHwndParent = -8;
    private const long WsExToolWindow = 0x00000080;

    private readonly nint _handle;
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _suppressSlider;
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

    private void ApplyLayout(MainWindow owner)
    {
        var bounds = owner.AppWindow.Position;
        var size = owner.AppWindow.Size;
        var scale = owner.Content?.XamlRoot?.RasterizationScale ?? 1.0;

        var width = Math.Min((int)Math.Round(BarWidthDip * scale), Math.Max(200, size.Width - 80));
        var height = (int)Math.Round(BarHeightDip * scale);

        var x = bounds.X + ((size.Width - width) / 2);
        var y = bounds.Y + size.Height - height - (int)Math.Round(BottomMarginDip * scale);

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));

        // Clip the window itself to the card's rounded shape. Without this the rounded border would
        // sit on a rectangular slab of window background, which is what made the bar look broken
        // over video. A window region cuts the corners for real: nothing is painted there at all.
        var radius = (int)Math.Round(CornerRadiusDip * scale);
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
