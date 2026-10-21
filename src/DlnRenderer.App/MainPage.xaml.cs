using DlnRenderer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DlnRenderer;

/// <summary>
/// Player page. Playback and DLNA discovery run in the renderer service, so this page only shows the
/// pushed state and forwards transport commands over the local pipe.
/// </summary>
public sealed partial class MainPage : Page
{
    private bool _suppressPositionSync;
    private bool _suppressVolumeSync;
    private bool _initialized;

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
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Navigating to settings unloads this page; staying subscribed would leak one handler per
        // visit and keep updating a view nobody can see.
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
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
