using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DlnRenderer;

/// <summary>
/// The application window: a title bar, a compact navigation pane and a frame for the pages.
/// </summary>
/// <remarks>
/// Closing the window hides it when the user chose "close to tray"; playback and discovery live in
/// the service and are unaffected either way. Only <see cref="App.ExitApplicationAsync"/> closes the
/// window for real, because it also stops the service.
/// </remarks>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Closing += OnClosing;

        RootFrame.Navigate(typeof(MainPage));
        Navigation.SelectedItem = Navigation.MenuItems[0];
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (App.IsExiting)
        {
            return;
        }

        if (App.Settings.CloseToTray)
        {
            // Hide instead of closing: the tray icon is the way back.
            args.Cancel = true;
            sender.Hide();
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        var target = tag switch
        {
            "settings" => typeof(SettingsPage),
            _ => typeof(MainPage),
        };

        if (RootFrame.CurrentSourcePageType != target)
        {
            RootFrame.Navigate(target);
        }
    }
}
