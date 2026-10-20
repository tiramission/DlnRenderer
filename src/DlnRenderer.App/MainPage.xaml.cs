using DlnRenderer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DlnRenderer;

/// <summary>
/// Minimal status page. Playback and DLNA discovery run in the renderer service, so this page only
/// shows the pushed state and forwards transport commands over the local pipe.
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
    }

    private async void OnPlayClick(object sender, RoutedEventArgs e) => await ViewModel.PlayAsync();

    private async void OnPauseClick(object sender, RoutedEventArgs e) => await ViewModel.PauseAsync();

    private async void OnStopClick(object sender, RoutedEventArgs e) => await ViewModel.StopAsync();

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
                _suppressPositionSync = true;
                PositionSlider.Maximum = Math.Max(1, ViewModel.DurationSeconds);
                PositionSlider.Value = ViewModel.PositionSeconds;
                _suppressPositionSync = false;
                break;
            case nameof(MainPageViewModel.Volume):
            case nameof(MainPageViewModel.DurationSeconds):
                _suppressVolumeSync = true;
                VolumeSlider.Value = ViewModel.Volume;
                _suppressVolumeSync = false;
                break;
        }
    }
}
