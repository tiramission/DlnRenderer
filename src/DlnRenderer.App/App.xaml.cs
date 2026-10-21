using DlnRenderer.Core.Ipc;
using DlnRenderer.Core.Models;
using DlnRenderer.Core.Settings;
using DlnRenderer.Services;
using Microsoft.UI.Xaml;

namespace DlnRenderer;

/// <summary>
/// Application entry point. Owns the long-lived pieces the window, the tray icon and the settings
/// page share: the settings snapshot and the single service connection.
/// </summary>
public partial class App : Application
{
    /// <summary>The main application window.</summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>The UI thread dispatcher; used to marshal pipe callbacks onto the UI thread.</summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>The native window handle (HWND).</summary>
    public static nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public static MainWindow? MainWindow { get; private set; }

    /// <summary>Settings as last loaded or saved. The service reloads the file on its own start.</summary>
    public static RendererSettings Settings { get; private set; } = RendererSettings.Default;

    public static SettingsStore SettingsStore { get; } = new();

    /// <summary>One pipe connection shared by the window, the tray icon and the settings page.</summary>
    public static ServiceClient Client { get; private set; } = null!;

    internal static TrayIcon? Tray { get; private set; }

    /// <summary>True once the user asked to exit; the window must then really close.</summary>
    public static bool IsExiting { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        DiagnosticsLog.Configure(GetOptionValue(commandLine, "--log"));

        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Settings = SettingsStore.Load();

        Client = new ServiceClient();
        Client.SnapshotChanged += OnSnapshotChanged;

        MainWindow = new MainWindow();
        Window = MainWindow;

        // A tray failure must not prevent the window from opening: the app is still usable without
        // it, and the reason is recorded instead of crashing at launch.
        try
        {
            Tray = new TrayIcon();
            Tray.OpenRequested += ShowMainWindow;
            Tray.ExitRequested += () => _ = ExitApplicationAsync();
            Tray.SetState("DlnRenderer", "正在连接后台服务");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"tray icon unavailable: {ex.Message}");
            Tray = null;
        }

        Window.Activate();
    }

    /// <summary>Brings the window back from the tray.</summary>
    public static void ShowMainWindow()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.AppWindow.Show();
        MainWindow.Activate();
    }

    /// <summary>Reloads settings after the settings page saved them.</summary>
    public static void ReloadSettings() => Settings = SettingsStore.Load();

    /// <summary>
    /// Stops the service and exits the UI. This is the only path that stops playback: closing the
    /// window and the tray menu's "open" both leave the service running.
    /// </summary>
    public static async Task ExitApplicationAsync()
    {
        if (IsExiting)
        {
            return;
        }

        IsExiting = true;

        try
        {
            if (Client.IsConnected)
            {
                await Client.SendAsync(IpcCommands.Shutdown).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"shutdown request failed: {ex.Message}");
        }

        Tray?.Dispose();
        Tray = null;

        await Client.DisposeAsync().ConfigureAwait(true);

        MainWindow?.Close();
        Current.Exit();
    }

    private static void OnSnapshotChanged(object? sender, RendererSnapshot snapshot)
        => DispatcherQueue.TryEnqueue(() => Tray?.SetState("DlnRenderer", Describe(snapshot.Playback)));

    private static string Describe(PlaybackStatus status) => status.State switch
    {
        PlaybackState.Playing when status.Media is not null => $"播放中 · {status.Media.Title ?? "未命名媒体"}",
        PlaybackState.Playing => "播放中",
        PlaybackState.Paused => "已暂停",
        PlaybackState.Buffering => "缓冲中",
        PlaybackState.Transitioning => "正在加载",
        PlaybackState.Error => "播放错误",
        _ => status.Media is null ? "待机" : "已停止",
    };

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
