using System.Runtime.InteropServices;
using DlnRenderer.Core.Ipc;
using DlnRenderer.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
/// It is borderless, has no taskbar entry and is positioned over the bottom of the main window. It
/// drives the same service as the page does, so the phone and the two UIs can never disagree: every
/// command goes over the pipe and the state comes back as a pushed snapshot.
/// </para>
/// </remarks>
public sealed partial class TransportOverlayWindow : Window
{
    /// <summary>Bar size in DIPs; small enough to sit over a film without hiding much.</summary>
    private const int BarWidthDip = 620;

    private const int BarHeightDip = 64;

    private const int BottomMarginDip = 28;

    private const int GwlExStyle = -20;
    private const int GwlHwndParent = -8;
    private const long WsExToolWindow = 0x00000080;

    private readonly nint _handle;
    private bool _suppressSlider;
    private nint _owner;

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

        App.Client.SnapshotChanged += OnSnapshotChanged;
        Closed += (_, _) => App.Client.SnapshotChanged -= OnSnapshotChanged;
    }

    /// <summary>
    /// Shows the bar over <paramref name="owner"/>'s bottom edge, or brings it to the front.
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
        ApplySnapshot(App.Client.Snapshot);
    }

    /// <summary>Moves the bar to follow the window it belongs to.</summary>
    public void FollowOwner(MainWindow owner)
    {
        if (_owner == 0 || AppWindow.IsVisible is false)
        {
            return;
        }

        ApplyLayout(owner);
    }

    public void HideOverlay() => AppWindow.Hide();

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
    }

    // ---- transport ---------------------------------------------------------------------------

    private async void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        var playing = App.Client.Snapshot?.Playback.State == PlaybackState.Playing;
        await SendAsync(playing ? IpcCommands.Pause : IpcCommands.Play);
    }

    private async void OnStopClick(object sender, RoutedEventArgs e) => await SendAsync(IpcCommands.Stop);

    private async void OnMuteClick(object sender, RoutedEventArgs e)
    {
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
        => DispatcherQueue.TryEnqueue(() => ApplySnapshot(snapshot));

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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
