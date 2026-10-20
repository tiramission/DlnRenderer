using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace DlnRenderer;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Optional media file supplied on the command line (<c>--play &lt;path&gt;</c> or a bare path).
    /// Used for scripted playback verification.
    /// </summary>
    public static string? StartupMediaPath { get; private set; }

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        StartupMediaPath = ParseStartupMediaPath(commandLine);
        DiagnosticsLog.Configure(GetOptionValue(commandLine, "--log"));

        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.Activate();
    }

    private static string? ParseStartupMediaPath(string[] commandLine)
    {
        var explicitPath = GetOptionValue(commandLine, "--play");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        for (var i = 1; i < commandLine.Length; i++)
        {
            if (!commandLine[i].StartsWith('-') && File.Exists(commandLine[i]))
            {
                return commandLine[i];
            }
        }

        return null;
    }

    private static string? GetOptionValue(string[] commandLine, string name)
    {
        for (var i = 1; i < commandLine.Length - 1; i++)
        {
            if (string.Equals(commandLine[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return commandLine[i + 1];
            }
        }

        return null;
    }
}
