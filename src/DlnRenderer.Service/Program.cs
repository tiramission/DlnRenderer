using System.Net;
using DlnRenderer.Core.Settings;
using DlnRenderer.Dlna;
using DlnRenderer.Player.Mpv;
using DlnRenderer.Service.Hosting;
using DlnRenderer.Service.Ipc;

namespace DlnRenderer.Service;

/// <summary>
/// Renderer service entry point. Runs independently of the WinUI process so closing the UI does
/// not stop playback or discovery.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!ServiceStartupOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine(parseError);
            Console.Error.WriteLine(ServiceStartupOptions.Usage);
            return 2;
        }

        // The UI writes settings; the service reads them at start. Command-line flags still win,
        // so a developer can override without editing the user's file.
        options = options.ApplySettings(new SettingsStore().Load());

        var log = new ServiceLog(options.LogPath);
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void RequestShutdown(string reason)
        {
            if (shutdown.TrySetResult())
            {
                log.Write($"shutdown requested: {reason}");
            }
        }

        // When the SCM starts this process (a service registered by hand with `sc create`; the
        // shipped MSIX package deliberately registers none) it expects a SERVICE_RUNNING report
        // within 30 seconds and then owns the stop decision. Without that
        // report the SCM logs event 7009 and marks the service failed while it is in fact serving
        // requests - "looks broken but works", which is the worst state to ship.
        //
        // StartServiceCtrlDispatcher calls the ServiceMain callback on THIS thread and only returns
        // when the SCM asks the service to stop. So the renderer has to be started from inside that
        // callback, and everything it needs is captured in the closure below.
        ServiceStatusReporter? scm = null;
        Task? renderer = null;

        void RunService()
        {
            // The dispatcher thread must return to the SCM promptly, so the renderer runs as a
            // detached task and reports its own status once it is listening.
            renderer = RunRendererAsync(options, log, shutdown, scm!, RequestShutdown);
        }

        scm = new ServiceStatusReporter(log, RunService, () => RequestShutdown("service control manager"));

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            RequestShutdown("ctrl+c");
        };

        // Blocks for the lifetime of the service when the SCM started us, and returns immediately
        // otherwise. Note that a return does NOT mean "stop requested": the dispatcher returns as
        // soon as ServiceMain does, which is right after the renderer has been handed off. The
        // renderer task itself is what has to be awaited before the process may exit.
        scm.ConnectAndWait();

        if (renderer is null)
        {
            // No SCM: the UI or a developer's shell started us, so run in the foreground exactly
            // as this console app always has.
            renderer = RunRendererAsync(options, log, shutdown, scm, RequestShutdown);
        }

        try
        {
            await renderer.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // RunRendererAsync already logs and swallows its own failures; reaching here means
            // something escaped it, which must not take the process down silently.
            log.Write($"renderer task failed: {ex}");
            Console.Error.WriteLine($"DlnRenderer.Service failed: {ex.Message}");
        }

        return 0;
    }

    /// <summary>
    /// Startup, wait-for-shutdown and teardown. Shared by the SCM path and the console path so the
    /// two cannot drift apart: only who drives them differs.
    /// </summary>
    private static async Task RunRendererAsync(
        ServiceStartupOptions options,
        ServiceLog log,
        TaskCompletionSource shutdown,
        ServiceStatusReporter scm,
        Action<string> requestShutdown)
    {
        // Null until each stage succeeds, so teardown only disposes what was actually created.
        var running = new RunningComponents();

        try
        {
            await StartAsync(options, log, running, requestShutdown).ConfigureAwait(false);

            // Everything is listening now, so the start is a success as far as the SCM is concerned.
            scm.ReportRunning();

            if (options.ExitAfter is { } exitAfter)
            {
                _ = Task.Delay(exitAfter).ContinueWith(_ => requestShutdown("--exit-after"), TaskScheduler.Default);
            }

            await shutdown.Task.ConfigureAwait(false);
            log.Write("service stopping");
        }
        catch (Exception ex)
        {
            log.Write($"startup failed: {ex}");
            Console.Error.WriteLine($"DlnRenderer.Service failed to start: {ex.Message}");
        }
        finally
        {
            // The terminal status has to reach the SCM before the slow part of teardown: reporting
            // SERVICE_STOPPED is what lets StartServiceCtrlDispatcher return, and waiting until
            // after the player has been disposed would leave the service in STOP_PENDING for as
            // long as disposal takes.
            scm.ReportStopped();
            scm.ReportServiceStopped();

            // Reverse of startup order: stop accepting commands, then discovery, then playback,
            // then the video surface the player was drawing into.
            await running.DisposeAsync().ConfigureAwait(false);
            scm.Dispose();
            log.Write("service stopped");
        }
    }


    /// <summary>
    /// Creates the video surface, player, DLNA host and IPC server, and publishes the endpoints.
    /// </summary>
    /// <remarks>
    /// Each component is stored on <paramref name="running"/> as soon as it exists, so a failure
    /// part-way through still leaves everything already created eligible for teardown.
    /// </remarks>
    private static async Task StartAsync(
        ServiceStartupOptions options,
        ServiceLog log,
        RunningComponents running,
        Action<string> requestShutdown)
    {
        var uuid = DeviceIdentity.LoadOrCreate(ServicePaths.DeviceStateFile);
        DeviceIdentity.Save(ServicePaths.DeviceStateFile, uuid, options.FriendlyName);

        log.Write($"service starting (pid {Environment.ProcessId}, udn uuid:{uuid:D})");
        log.Write(
            $"launch: {(options.LaunchedByServiceControlManager ? "service control manager" : "interactive/ui")}, " +
            $"state directory {ServicePaths.DataDirectory}");
        log.Write($"settings: device name '{options.FriendlyName}', hwdec={options.HardwareDecoding}");

        var presenter = await VideoPresenter.StartAsync().ConfigureAwait(false);
        running.Presenter = presenter;
        log.Write($"video surface created (hwnd 0x{presenter.ChildHandle:X})");

        var player = CreatePlayer(options, presenter.ChildHandle, log);
        running.Player = player;

        var dlnaOptions = new DlnaOptions
        {
            FriendlyName = options.FriendlyName,
            Uuid = uuid,
            HttpPort = options.HttpPort,
            ListenAddress = options.ListenAddress,
            EnableSsdp = options.Ssdp,
        };

        var session = new RendererSession(player, presenter, dlnaOptions, log.Write);
        running.Session = session;

        var host = new DlnaDeviceHost(dlnaOptions, session, log.Write);
        running.Host = host;
        ConnectEventing(session, host);

        host.Start();
        session.DiscoveryActive = host.SsdpActive;
        session.ControlUrl = host.ControlUrl;

        var ipc = new IpcServer(session, log, () => requestShutdown("ipc shutdown command"));
        ipc.Start();
        running.Ipc = ipc;

        log.Write($"device description: {host.DescriptionUrl}");
        log.Write($"ssdp active: {host.SsdpActive} on {string.Join(", ", host.SsdpAddresses)}");
        Console.WriteLine($"READY {host.DescriptionUrl}");
    }

    /// <summary>Creates the libmpv player bound to the service's video surface.</summary>
    /// <remarks>
    /// <c>DLN_MPV_LOGLEVEL</c> exists so a developer can raise mpv's verbosity without rebuilding;
    /// a rejected file is otherwise only visible as "loading failed".
    /// </remarks>
    private static MpvPlayer CreatePlayer(ServiceStartupOptions options, nint videoWindow, ServiceLog log)
    {
        var player = new MpvPlayer(new MpvPlayerOptions
        {
            VideoWindowHandle = videoWindow,
            HardwareDecoding = options.HardwareDecoding,
            LogLevel = Environment.GetEnvironmentVariable("DLN_MPV_LOGLEVEL") ?? "warn",
        });

        // A rejected file is only diagnosable through mpv's own log: the player reports "loading
        // failed" and Windows does not say why. Forwarding it costs nothing and turns a silent
        // black screen into a line the user can quote in a bug report.
        player.LogMessage += (_, message) => log.Write($"mpv {message.Level}: {message.Text}");
        log.Write("libmpv initialised");

        return player;
    }

    /// <summary>
    /// Feeds GENA from the session's own state changes.
    /// </summary>
    /// <remarks>
    /// The same <see cref="DlnRenderer.Core.Models.PlaybackStatus"/> drives the IPC snapshot and the
    /// events, so a subscriber and the UI can never disagree about what is playing.
    /// </remarks>
    private static void ConnectEventing(RendererSession session, DlnaDeviceHost host)
    {
        var eventing = host.Eventing;

        session.TransportEventRaised += status =>
            eventing.UpdateAvTransport(status.State, status.ErrorMessage, status.Duration);
        session.RenderingEventRaised += (volume, muted) => eventing.UpdateRenderingControl(volume, muted);
        session.MediaReplaced += eventing.NoteMediaChanged;
    }

    /// <summary>
    /// Holds the components created during startup so teardown can be ordered and fault tolerant.
    /// </summary>
    private sealed class RunningComponents : IAsyncDisposable
    {
        public VideoPresenter? Presenter { get; set; }

        public MpvPlayer? Player { get; set; }

        public RendererSession? Session { get; set; }

        public DlnaDeviceHost? Host { get; set; }

        public IpcServer? Ipc { get; set; }

        /// <summary>
        /// Disposes in reverse creation order. Each step is isolated: a failure in one must not
        /// prevent the others from releasing sockets, pipes or the native player.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await DisposeOneAsync(Ipc).ConfigureAwait(false);
            await DisposeOneAsync(Host).ConfigureAwait(false);
            await DisposeOneAsync(Session).ConfigureAwait(false);
            await DisposeOneAsync(Player).ConfigureAwait(false);
            await DisposeOneAsync(Presenter).ConfigureAwait(false);
        }

        private static async ValueTask DisposeOneAsync(IAsyncDisposable? disposable)
        {
            if (disposable is null)
            {
                return;
            }

            try
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Teardown must run to completion; a component that throws here would otherwise
                // strand the ones after it.
            }
        }
    }
}

internal sealed record ServiceStartupOptions
{
    public const string Usage =
        "Usage: DlnRenderer.Service [--http-port N] [--no-ssdp] [--device-name NAME] " +
        "[--listen-address IP] [--log PATH] [--exit-after SECONDS] [--no-hwdec] [--service]";

    public string FriendlyName { get; init; } = RendererSettings.DefaultDeviceName;

    /// <summary>
    /// True when started by the Service Control Manager rather than by the UI or a developer's
    /// shell.
    /// </summary>
    /// <remarks>
    /// Behaviour is the same in both modes; the flag only makes the launch path visible in the log,
    /// which matters when diagnosing why state landed in <c>%ProgramData%</c> instead of the user
    /// profile (see <see cref="ServicePaths"/>).
    /// </remarks>
    public bool LaunchedByServiceControlManager { get; init; }

    /// <summary>True when <c>--device-name</c> was supplied, so settings must not override it.</summary>
    public bool DeviceNameSpecified { get; init; }

    /// <summary>
    /// Stable default HTTP port. A fixed port keeps the advertised LOCATION identical across
    /// restarts, which is what firewall rules, control-point caches and bookmarks depend on.
    /// Pass <c>--http-port 0</c> to fall back to an ephemeral port.
    /// </summary>
    public const int DefaultHttpPort = 18090;

    public int HttpPort { get; init; } = DefaultHttpPort;

    public bool Ssdp { get; init; } = true;

    public bool HardwareDecoding { get; init; } = true;

    /// <summary>True when <c>--no-hwdec</c> was supplied, so settings must not override it.</summary>
    public bool HardwareDecodingSpecified { get; init; }

    public IPAddress? ListenAddress { get; init; }

    public string? LogPath { get; init; } = ServicePaths.NewLogFile();

    public TimeSpan? ExitAfter { get; init; }

    public static bool TryParse(string[] args, out ServiceStartupOptions options, out string? error)
    {
        var result = new ServiceStartupOptions();
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];

            // Every flag either sets the whole result or fails with one message; keeping that shape
            // in one helper is what makes the list below readable as a table of options.
            if (!TryApply(args, ref i, argument, result, out var updated, out error))
            {
                options = result;
                return false;
            }

            result = updated;
        }

        options = result;
        return true;
    }

    /// <summary>
    /// Fills the options that were not given on the command line from persisted settings.
    /// </summary>
    /// <remarks>
    /// Explicit flags always win: the UI starts the service without flags, while a developer or a
    /// diagnostic script may need to override a stored value for one run without editing it.
    /// </remarks>
    public ServiceStartupOptions ApplySettings(RendererSettings settings)
        => this with
        {
            FriendlyName = DeviceNameSpecified ? FriendlyName : settings.DeviceName,
            HardwareDecoding = HardwareDecodingSpecified ? HardwareDecoding : settings.HardwareDecoding,
        };

    /// <summary>
    /// Applies one command-line argument.
    /// </summary>
    /// <param name="args">Full argument list.</param>
    /// <param name="index">Index of the current argument; advanced when the flag takes a value.</param>
    /// <param name="argument">The flag being applied.</param>
    /// <param name="current">Options accumulated so far.</param>
    /// <param name="updated">Options after this flag.</param>
    /// <param name="error">Message describing why the flag was rejected.</param>
    /// <returns>False when the flag is unknown or its value is invalid.</returns>
    private static bool TryApply(
        string[] args,
        ref int index,
        string argument,
        ServiceStartupOptions current,
        out ServiceStartupOptions updated,
        out string? error)
    {
        error = null;
        updated = current;

        switch (argument)
        {
            case "--service":
                // Not passed by the shipped package (the MSIX channel is per-user and registers no
                // service). Kept for a service registered by hand with `sc create`: the flag makes
                // the launch path visible in the log and gives a future service-only
                // behaviour (no console, SCM stop events) a place to hang.
                updated = current with { LaunchedByServiceControlManager = true };
                return true;

            case "--no-ssdp":
                updated = current with { Ssdp = false };
                return true;

            case "--no-hwdec":
                updated = current with { HardwareDecoding = false, HardwareDecodingSpecified = true };
                return true;

            case "--no-log":
                updated = current with { LogPath = null };
                return true;

            case "--http-port":
                if (!TryRead(args, ref index, out var portText)
                    || !int.TryParse(portText, out var port)
                    || port is < 0 or > 65535)
                {
                    error = "--http-port requires a port number between 0 and 65535";
                    return false;
                }

                updated = current with { HttpPort = port };
                return true;

            case "--device-name":
                if (!TryRead(args, ref index, out var name) || string.IsNullOrWhiteSpace(name))
                {
                    error = "--device-name requires a non-empty value";
                    return false;
                }

                updated = current with { FriendlyName = name.Trim(), DeviceNameSpecified = true };
                return true;

            case "--listen-address":
                if (!TryRead(args, ref index, out var addressText)
                    || !IPAddress.TryParse(addressText, out var address))
                {
                    error = "--listen-address requires an IPv4 address";
                    return false;
                }

                updated = current with { ListenAddress = address };
                return true;

            case "--log":
                if (!TryRead(args, ref index, out var logPath))
                {
                    error = "--log requires a path";
                    return false;
                }

                updated = current with { LogPath = logPath };
                return true;

            case "--exit-after":
                if (!TryRead(args, ref index, out var secondsText)
                    || !double.TryParse(secondsText, out var seconds)
                    || seconds <= 0)
                {
                    error = "--exit-after requires a positive number of seconds";
                    return false;
                }

                updated = current with { ExitAfter = TimeSpan.FromSeconds(seconds) };
                return true;

            default:
                error = $"unknown argument '{argument}'";
                return false;
        }
    }
    private static bool TryRead(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }
}
