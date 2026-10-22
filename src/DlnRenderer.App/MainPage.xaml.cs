using DlnRenderer.Core.Ipc;
using DlnRenderer.Core.Models;
using DlnRenderer.Services;
using DlnRenderer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DlnRenderer;

/// <summary>
/// Player page. Playback and DLNA discovery run in the renderer service, so this page only shows the
/// pushed state and forwards transport commands over the local pipe.
/// </summary>
/// <remarks>
/// In "UI window" mode the page also owns the geometry contract: it tells the service that the video
/// may cover the whole window below the title bar, in physical pixels. Only the UI knows its DPI and
/// how much room the title bar takes, so the service never has to guess.
/// <para>
/// The page cannot draw controls over that picture - a Win32 child window is always painted above
/// its parent's XAML - so the transport bar lives in <see cref="TransportOverlayWindow"/> and this
/// page only decides when it should be visible.
/// </para>
/// </remarks>
public sealed partial class MainPage : Page
{
    private bool _suppressPositionSync;
    private bool _suppressVolumeSync;
    private bool _initialized;

    /// <summary>Last geometry sent, so a burst of layout passes costs one message.</summary>
    private VideoTarget? _reportedTarget;

    public MainPageViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnPageSizeChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        if (XamlRoot is { } root)
        {
            // A DPI change (dragging the window to another monitor) moves the rectangle in physical
            // pixels without changing any layout size, so no SizeChanged would fire.
            root.Changed += OnXamlRootChanged;
        }

        ReportVideoTarget(force: true);

        try
        {
            await ViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            // A silent, permanently-disconnected UI is worse than a visible error.
            DiagnosticsLog.Write($"service connect failed: {ex}");
        }

        // The sliders are not bound: their values are pushed in code so a user drag and a snapshot
        // update cannot feed each other. The first snapshot may carry the values the view model
        // already had (100% volume), which raises no property change, so sync once explicitly.
        SyncControls();

        // The service may still be starting when the first report was sent; repeat once the
        // connection is up so a cast that starts immediately still lands in the window.
        ReportVideoTarget(force: true);
        UpdateTransportOverlay();

        // The title bar's height is what the picture must leave free, and it is only known after the
        // first layout pass. One extra report once that pass has happened keeps the first cast of a
        // session from covering the window controls.
        LayoutUpdated += OnFirstLayoutUpdated;
    }

    private void OnFirstLayoutUpdated(object? sender, object e)
    {
        LayoutUpdated -= OnFirstLayoutUpdated;
        ReportVideoTarget(force: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Navigating to settings unloads this page; staying subscribed would leak one handler per
        // visit and keep updating a view nobody can see.
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        if (XamlRoot is { } root)
        {
            root.Changed -= OnXamlRootChanged;
        }

        App.MainWindow?.SetTransportOverlayVisible(false);

        // The rectangle this page reserved is gone. Sending a host-less target makes the service
        // detach and fall back to its own window instead of leaving a child window floating over a
        // page that no longer exists.
        _ = SendVideoTargetAsync(new VideoTarget { Mode = App.Settings.DisplayMode });

        ViewModel.Dispose();
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e) => ReportVideoTarget();

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ReportVideoTarget(force: true);

    /// <summary>
    /// Shows the floating transport bar while an embedded cast has a picture on screen.
    /// </summary>
    private void UpdateTransportOverlay()
    {
        var embedded = App.Settings.DisplayMode == DisplayMode.Embedded;
        App.MainWindow?.SetTransportOverlayVisible(embedded && ViewModel.ShowsVideo);
    }

    /// <summary>Reports the current reserved rectangle to the service, if it changed.</summary>
    private void ReportVideoTarget(bool force = false)
    {
        if (!_initialized)
        {
            return;
        }

        var target = BuildVideoTarget();
        if (!force && target == _reportedTarget)
        {
            return;
        }

        _reportedTarget = target;
        _ = SendVideoTargetAsync(target);
    }

    private async Task SendVideoTargetAsync(VideoTarget target)
    {
        try
        {
            await App.Client.SendAsync(IpcCommands.SetVideoTarget, videoTarget: target);
        }
        catch (Exception ex)
        {
            // A dropped pipe is reported by the client itself; geometry is not worth surfacing.
            DiagnosticsLog.Write($"video target report failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The rectangle an embedded cast may cover.
    /// </summary>
    /// <remarks>
    /// In a window the picture takes over the page's own area - the content region inside the
    /// navigation view - so the title bar and the navigation pane stay usable and the user can still
    /// reach settings mid-film. In fullscreen the whole client area is used, because there is nothing
    /// left worth keeping.
    /// </remarks>
    private VideoTarget BuildVideoTarget()
    {
        var mode = App.Settings.DisplayMode;

        if (mode != DisplayMode.Embedded)
        {
            return new VideoTarget { Mode = mode };
        }

        var root = XamlRoot;
        if (root is null)
        {
            return new VideoTarget { Mode = mode, HostWindow = (long)App.WindowHandle };
        }

        var scale = root.RasterizationScale;
        double left;
        double top;
        double width;
        double height;

        if (App.MainWindow?.IsFullScreen == true)
        {
            left = 0;
            top = 0;
            width = root.Size.Width;
            height = root.Size.Height;
        }
        else
        {
            // The page's own bounds already exclude the title bar and the navigation pane, and they
            // follow any layout change without the page having to know the window's structure.
            var bounds = TransformToVisual(null).TransformBounds(new Rect(0, 0, ActualWidth, ActualHeight));
            left = bounds.X;
            top = bounds.Y;
            width = bounds.Width;
            height = bounds.Height;
        }

        if (width < 2 || height < 2)
        {
            return new VideoTarget { Mode = mode, HostWindow = (long)App.WindowHandle };
        }

        return new VideoTarget
        {
            Mode = mode,
            HostWindow = (long)App.WindowHandle,
            X = (int)Math.Round(left * scale),
            Y = (int)Math.Round(top * scale),
            Width = (int)Math.Round(width * scale),
            Height = (int)Math.Round(height * scale),
        };
    }

    private async void OnPlayClick(object sender, RoutedEventArgs e) => await ViewModel.PlayAsync();

    private async void OnPauseClick(object sender, RoutedEventArgs e) => await ViewModel.PauseAsync();

    private async void OnStopClick(object sender, RoutedEventArgs e) => await ViewModel.StopAsync();

    private async void OnMuteClick(object sender, RoutedEventArgs e) => await ViewModel.ToggleMuteAsync();

    private async void OnPositionSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressPositionSync || !_initialized)
        {
            return;
        }

        // Ignore sub-second drift caused by periodic position snapshots.
        if (Math.Abs(e.NewValue - ViewModel.PositionSeconds) < 1.0)
        {
            return;
        }

        await ViewModel.SeekAsync(e.NewValue);
    }

    private async void OnVolumeSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressVolumeSync || !_initialized)
        {
            return;
        }

        await ViewModel.SetVolumeAsync(e.NewValue);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainPageViewModel.PositionSeconds):
            case nameof(MainPageViewModel.DurationSeconds):
                SyncPosition();
                break;
            case nameof(MainPageViewModel.Volume):
                _suppressVolumeSync = true;
                VolumeSlider.Value = ViewModel.Volume;
                _suppressVolumeSync = false;
                break;
            case nameof(MainPageViewModel.ShowsVideo):
                UpdateTransportOverlay();
                break;
        }
    }

    private void SyncControls()
    {
        SyncPosition();

        _suppressVolumeSync = true;
        VolumeSlider.Value = ViewModel.Volume;
        _suppressVolumeSync = false;
    }

    private void SyncPosition()
    {
        _suppressPositionSync = true;
        PositionSlider.Maximum = Math.Max(1, ViewModel.DurationSeconds);
        PositionSlider.Value = ViewModel.PositionSeconds;
        _suppressPositionSync = false;
    }
}
