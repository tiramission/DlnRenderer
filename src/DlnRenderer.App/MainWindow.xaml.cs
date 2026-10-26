using System.Runtime.InteropServices;
using DlnRenderer.Core.Ipc;
using DlnRenderer.Core.Models;
using DlnRenderer.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

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

    /// <summary>The embedded video area in client coordinates, as last reported by the player page.</summary>
    private VideoTarget? _stageTarget;

    /// <summary>
    /// The video area translated to screen coordinates, recomputed on every window move. Zero size
    /// means "unknown": fullscreen casts or a page that has not reported yet.
    /// </summary>
    private RectInt32 _stageScreenRect;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Closing += OnClosing;
        AppWindow.Changed += OnAppWindowChanged;

        // Escape leaves fullscreen when the window has the keyboard. The floating bar is the primary
        // way out (it appears on any mouse move), but a keyboard exit costs nothing and is what
        // every fullscreen player does.
        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.Escape,
        };
        escape.Invoked += (_, args) =>
        {
            if (!_fullScreen)
            {
                return;
            }

            SetFullScreen(false);
            args.Handled = true;
        };
        RootGrid.KeyboardAccelerators.Add(escape);

        RootFrame.Navigate(typeof(MainPage));
        Navigation.SelectedItem = Navigation.MenuItems[0];
    }

    public bool IsFullScreen => _fullScreen;

    /// <summary>Screen rectangle of the embedded video area, or a zero rect when there is none.</summary>
    public RectInt32 StageScreenRect => _stageScreenRect;

    /// <summary>
    /// Records where the embedded picture lives, so the floating transport bar can hug the stage
    /// instead of the window's bottom edge. The player page calls this every time it re-reports
    /// its geometry.
    /// </summary>
    /// <remarks>
    /// The rectangle is kept client-relative and translated to screen coordinates on demand: a
    /// window drag changes the screen position but not the client rectangle, and the window thread
    /// knows each move before the page could re-report it.
    /// </remarks>
    public void UpdateStageRect(VideoTarget target)
    {
        _stageTarget = target.CanEmbed ? target : null;
        ApplyStageRect();
    }

    private void ApplyStageRect()
    {
        var target = _stageTarget;
        if (target is null)
        {
            _stageScreenRect = default;
            return;
        }

        var origin = new NativePoint { X = 0, Y = 0 };
        if (!ClientToScreen(App.WindowHandle, ref origin))
        {
            _stageScreenRect = default;
            return;
        }

        _stageScreenRect = new RectInt32(
            origin.X + target.X,
            origin.Y + target.Y,
            target.Width,
            target.Height);
    }

    /// <summary>
    /// Navigates to the settings page. Used by the player page's firewall warning: selecting the
    /// item goes through the same navigation handler as a click in the pane.
    /// </summary>
    public void ShowSettings()
    {
        foreach (var entry in Navigation.MenuItems)
        {
            if (entry is NavigationViewItem { Tag: "settings" } item)
            {
                Navigation.SelectedItem = item;
                return;
            }
        }
    }

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
    /// <remarks>
    /// Fullscreen is deliberately <em>not</em> tied to this: stopping between two items of a cast
    /// hides the picture for a moment, and dropping out of fullscreen every time a new item starts
    /// would be worse than staying put. The page decides when the cast is really over.
    /// </remarks>
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
            // The stage's screen position moves with the window even though the client rectangle
            // does not; the bar is placed from the stage, so both need a fresh look.
            ApplyStageRect();
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

        if (RootFrame.CurrentSourcePageType == target)
        {
            return;
        }

        // Suspending the picture here rather than in the page's Unloaded handler makes it part of
        // the navigation itself: no matter how the framework orders unload and load, the service is
        // told before the new page appears. The page reports its rectangle again on load.
        if (target != typeof(MainPage))
        {
            SuspendVideoPresentation();
        }

        RootFrame.Navigate(target);
    }

    /// <summary>
    /// Tells the service the player page is no longer on screen, without changing the display mode.
    /// </summary>
    /// <remarks>
    /// Sends a suspended target, not a host-less one: a target without a host means "there is no UI"
    /// and is answered with the fullscreen overlay, which is not what opening the settings page is.
    /// </remarks>
    private static void SuspendVideoPresentation()
    {
        _ = SendSuspendedTargetAsync();
    }

    private static async Task SendSuspendedTargetAsync()
    {
        try
        {
            await App.Client.SendAsync(
                IpcCommands.SetVideoTarget,
                videoTarget: new VideoTarget { Mode = App.Settings.DisplayMode, Enabled = false });
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"suspend video target failed: {ex.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref NativePoint point);
}
