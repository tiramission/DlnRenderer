using DlnRenderer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DlnRenderer;

/// <summary>Settings page: edits the persisted renderer settings and offers an explicit exit.</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; } = new();

    public SettingsPage()
    {
        InitializeComponent();
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // Push the text box content explicitly: the TwoWay binding only commits on focus loss, so a
        // name typed and confirmed with the save button (or a keyboard shortcut) could be dropped.
        ViewModel.DeviceName = DeviceNameBox.Text;
        await ViewModel.SaveAsync();
    }

    private async void OnRestartClick(object sender, RoutedEventArgs e) => await ViewModel.RestartServiceAsync();

    private async void OnExitClick(object sender, RoutedEventArgs e) => await ViewModel.ExitAsync();
}
