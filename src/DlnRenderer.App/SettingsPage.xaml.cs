using System.Diagnostics;
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
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Fresh answer every visit: the network may have changed since the app started.
        await ViewModel.RefreshFirewallAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.Dispose();

    private async void OnRefreshFirewallClick(object sender, RoutedEventArgs e)
        => await ViewModel.RefreshFirewallAsync();

    private async void OnAllowDiscoveryClick(object sender, RoutedEventArgs e)
        => await ViewModel.AllowDiscoveryAsync();

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // Push the text box content explicitly: the TwoWay binding commits on focus loss, so a name
        // typed and confirmed with the save button (or a keyboard shortcut) could be dropped.
        ViewModel.DeviceName = DeviceNameBox.Text;
        await ViewModel.SaveAsync();
    }

    private void OnDeviceNameChanged(object sender, TextChangedEventArgs e)
    {
        // Keeps the command bar honest while typing: without this the footer would say "所有更改已保存"
        // until focus left the box, and the save button would stay disabled.
        ViewModel.DeviceName = DeviceNameBox.Text;
    }

    private async void OnRestartClick(object sender, RoutedEventArgs e) => await ViewModel.RestartServiceAsync();

    private async void OnExitClick(object sender, RoutedEventArgs e) => await ViewModel.ExitAsync();

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // The folder may not exist yet on a fresh install; creating it is the cheapest way to
            // make "open the folder" work instead of failing with a shell error.
            Directory.CreateDirectory(ViewModel.DataFolder);
            Process.Start(new ProcessStartInfo(ViewModel.DataFolder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ViewModel.ReportError($"无法打开数据目录：{ex.Message}");
        }
    }
}
