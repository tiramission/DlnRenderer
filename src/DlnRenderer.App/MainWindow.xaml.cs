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
/// <para>
/// This window also owns two things the pages cannot own themselves: the fullscreen presenter, and
/// the floating transport bar that has to live in its own window (see
/// <see cref="TransportOverlayWindow"/>). Ownership here keeps both in step with the window's
/// visibility, size and presenter, which a page cannot observe.
/// </para>
/// </remarks>
public sealed partial class MainWindow : Window
{
    private TransportOverlayWindow? _overlay;
    private bool _overlayWanted;
    private bool _fullScreen;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Closing += OnClosing;
        AppWindow.Changed += OnAppWindowChanged;

        RootFrame.Navigate(typeof(MainPage));
        Navigation.SelectedItem = Navigation.MenuItems[0];
    }

    public bool IsFullScreen => _fullScreen;

    /// <summary>
    /// Height, in DIPs, that an embedded cast must leave free at the top for the title bar.
    /// </summary>
    /// <remarks>
    /// Zero in fullscreen: the bar is collapsed there so the picture really covers the screen, and
    /// the floating transport bar is what gets the user back out.
    /// </remarks>
    public double TitleBarHeight => _fullScreen ? 0 : AppTitleBar.ActualHeight;

    /// <summary>Enters or leaves the borderless fullscreen presenter.</summary>
    public void SetFullScreen(bool fullScreen)
    {
        if (_fullScreen == fullScreen)
        {
            return;
        }

        _fullScreen = fullScreen;
        AppTitleBar.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);

        // The bar sits relative to the window, so it has to be placed again after the presenter
        // changed the window's rectangle.
        _overlay?.FollowOwner(this);
    }

    /// <summary>
    /// Shows or hides the floating transport bar. It is only ever shown while the window itself is
    /// visible, because hiding the window does not hide an owned window on its own.
    /// </summary>
    public void SetTransportOverlayVisible(bool visible)
    {
        _overlayWanted = visible;
        ApplyOverlay();
    }

    private void ApplyOverlay()
    {
        if (_overlayWanted && AppWindow.IsVisible)
        {
            _overlay ??= new TransportOverlayWindow();
            _overlay.ShowOver(this);
            return;
        }

        _overlay?.HideOverlay();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidVisibilityChange)
        {
            ApplyOverlay();
        }

        if (args.DidPositionChange || args.DidSizeChange)
        {
            _overlay?.FollowOwner(this);
        }

        if (args.DidPresenterChange)
        {
            // The user can leave fullscreen without going through our button (Win+Down, a window
            // manager gesture). Follow the real presenter instead of assuming we are still there.
            var fullScreen = sender.Presenter.Kind == AppWindowPresenterKind.FullScreen;
            if (fullScreen != _fullScreen)
            {
                _fullScreen = fullScreen;
                AppTitleBar.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (App.IsExiting)
        {
            // The app exits when its last window closes; a floating bar left behind would keep the
            // process alive with no way to reach it.
            _overlay?.Close();
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
