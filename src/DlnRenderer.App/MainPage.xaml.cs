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
/// This page also owns the geometry of embedded video: it reserves a rectangle and tells the service
/// where that rectangle is, in physical pixels relative to the window's client area. The service
/// then parents its video window into this window. Reporting coordinates in client space is what
/// keeps the service out of DPI maths - only the UI knows its scale factor and its layout.
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
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        ApplyDisplayMode();

        if (VideoSurface.XamlRoot is { } root)
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
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Navigating to settings unloads this page; staying subscribed would leak one handler per
        // visit and keep updating a view nobody can see.
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        if (VideoSurface.XamlRoot is { } root)
        {
            root.Changed -= OnXamlRootChanged;
        }

        // The rectangle this page reserved is gone. Sending a host-less target makes the service
        // detach and fall back to its own window instead of leaving a child window floating over a
        // page that no longer exists.
        _ = SendVideoTargetAsync(new VideoTarget { Mode = App.Settings.DisplayMode });

        ViewModel.Dispose();
    }

    /// <summary>Shows or collapses the reserved area to match the configured display mode.</summary>
    private void ApplyDisplayMode()
    {
        var embedded = App.Settings.DisplayMode == DisplayMode.Embedded;

        VideoRow.Height = embedded ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        VideoSurface.Visibility = embedded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnVideoSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => ReportVideoTarget();

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ReportVideoTarget(force: true);

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

    private VideoTarget BuildVideoTarget()
    {
        var mode = App.Settings.DisplayMode;

        if (mode != DisplayMode.Embedded || VideoSurface.Visibility != Visibility.Visible)
        {
            return new VideoTarget { Mode = mode };
        }

        var width = VideoSurface.ActualWidth;
        var height = VideoSurface.ActualHeight;
        var root = VideoSurface.XamlRoot;

        // Before the first layout pass there is no rectangle to report; the host is still sent so the
        // service can tell "not laid out yet" from "no UI at all".
        if (root is null || width < 2 || height < 2)
        {
            return new VideoTarget { Mode = mode, HostWindow = (long)App.WindowHandle };
        }

        var scale = root.RasterizationScale;
        var bounds = VideoSurface
            .TransformToVisual(null)
            .TransformBounds(new Rect(0, 0, width, height));

        return new VideoTarget
        {
            Mode = mode,
            HostWindow = (long)App.WindowHandle,
            X = (int)Math.Round(bounds.X * scale),
            Y = (int)Math.Round(bounds.Y * scale),
            Width = Math.Max(0, (int)Math.Round(bounds.Width * scale)),
            Height = Math.Max(0, (int)Math.Round(bounds.Height * scale)),
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
