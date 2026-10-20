using DlnRenderer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DlnRenderer;

/// <summary>
/// Minimal playback page: opens a local file, shows transport controls, and hosts the native
/// libmpv video window over the placeholder area.
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
        SizeChanged += (_, _) => UpdateVideoBounds();
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
            ViewModel.Initialize(App.WindowHandle);
        }
        catch (Exception ex)
        {
            // Surfacing the failure is the point: a silent black player is worse than an error.
            ViewModel.ReportInitializationFailure(ex.Message);
            return;
        }

        VolumeSlider.Value = ViewModel.Volume;
        UpdateVideoBounds();

        if (!string.IsNullOrWhiteSpace(App.StartupMediaPath))
        {
            // The command line switch means "play this", unlike picking a file in the UI.
            await ViewModel.OpenAsync(App.StartupMediaPath, "cli");
            await ViewModel.PlayAsync();
        }
    }

    /// <summary>Destroys the player before the window (and its child HWND) goes away.</summary>
    public void ShutdownPlayer() => ViewModel.Shutdown();

    private void UpdateVideoBounds()
    {
        var rasterizationScale = XamlRoot?.RasterizationScale ?? 1.0;
        var bounds = VideoArea
            .TransformToVisual(null)
            .TransformBounds(new Windows.Foundation.Rect(0, 0, VideoArea.ActualWidth, VideoArea.ActualHeight));

        ViewModel.SetVideoBounds(
            (int)Math.Round(bounds.X * rasterizationScale),
            (int)Math.Round(bounds.Y * rasterizationScale),
            (int)Math.Round(bounds.Width * rasterizationScale),
            (int)Math.Round(bounds.Height * rasterizationScale));
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.FileTypeFilter.Add(".mp4");
        picker.FileTypeFilter.Add(".mkv");
        picker.FileTypeFilter.Add(".webm");
        picker.FileTypeFilter.Add(".mp3");
        picker.FileTypeFilter.Add(".flac");
        picker.FileTypeFilter.Add(".wav");
        picker.FileTypeFilter.Add(".m4a");
        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await ViewModel.OpenAsync(file.Path, "picker");
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

        // Ignore sub-second drift caused by the player's periodic position updates.
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
