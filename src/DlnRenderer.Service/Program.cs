using System.Net;
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

        var log = new ServiceLog(options.LogPath);
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void RequestShutdown(string reason)
        {
            if (shutdown.TrySetResult())
            {
                log.Write($"shutdown requested: {reason}");
            }
        }

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            RequestShutdown("ctrl+c");
        };

        VideoPresenter? presenter = null;
        RendererSession? session = null;
        DlnaDeviceHost? host = null;
        IpcServer? ipc = null;

        try
        {
            var uuid = DeviceIdentity.LoadOrCreate(ServicePaths.DeviceStateFile);
            DeviceIdentity.Save(ServicePaths.DeviceStateFile, uuid, options.FriendlyName);

            log.Write($"service starting (pid {Environment.ProcessId}, udn uuid:{uuid:D})");

            presenter = await VideoPresenter.StartAsync().ConfigureAwait(false);
            log.Write($"video surface created (hwnd 0x{presenter.ChildHandle:X})");

            var player = new MpvPlayer(new MpvPlayerOptions
            {
                VideoWindowHandle = presenter.ChildHandle,
                HardwareDecoding = options.HardwareDecoding,
                LogLevel = Environment.GetEnvironmentVariable("DLN_MPV_LOGLEVEL") ?? "warn",
            });

            // A rejected file is only diagnosable through mpv's own log: the player reports "loading
            // failed" and Windows does not say why. Forwarding it costs nothing and turns a silent
            // black screen into a line the user can quote in a bug report.
            player.LogMessage += (_, message) => log.Write($"mpv {message.Level}: {message.Text}");
            log.Write("libmpv initialised");

            var dlnaOptions = new DlnaOptions
            {
                FriendlyName = options.FriendlyName,
                Uuid = uuid,
                HttpPort = options.HttpPort,
                ListenAddress = options.ListenAddress,
                EnableSsdp = options.Ssdp,
            };

            session = new RendererSession(player, presenter, dlnaOptions, log.Write);

            host = new DlnaDeviceHost(dlnaOptions, session, log.Write);

            // GENA is fed from the session's own state changes - the same values the IPC snapshot
            // exposes - so subscribers and the UI cannot disagree about what is playing.
            var eventing = host.Eventing;
            session.TransportEventRaised += status =>
                eventing.UpdateAvTransport(status.State, status.ErrorMessage, status.Duration);
            session.RenderingEventRaised += (volume, muted) => eventing.UpdateRenderingControl(volume, muted);
            session.MediaReplaced += eventing.NoteMediaChanged;

            host.Start();
            session.DiscoveryActive = host.SsdpActive;
            session.ControlUrl = host.ControlUrl;

            ipc = new IpcServer(session, log, () => RequestShutdown("ipc shutdown command"));
            ipc.Start();

            log.Write($"device description: {host.DescriptionUrl}");
            log.Write($"ssdp active: {host.SsdpActive} on {string.Join(", ", host.SsdpAddresses)}");
            Console.WriteLine($"READY {host.DescriptionUrl}");

            if (options.ExitAfter is { } exitAfter)
            {
                _ = Task.Delay(exitAfter).ContinueWith(_ => RequestShutdown("--exit-after"), TaskScheduler.Default);
            }

            await shutdown.Task.ConfigureAwait(false);
            log.Write("service stopping");
        }
        catch (Exception ex)
        {
            log.Write($"startup failed: {ex}");
            Console.Error.WriteLine($"DlnRenderer.Service failed to start: {ex.Message}");
            return 1;
        }
        finally
        {
            if (ipc is not null)
            {
                await ipc.DisposeAsync().ConfigureAwait(false);
            }

            if (host is not null)
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }

            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            if (presenter is not null)
            {
                await presenter.DisposeAsync().ConfigureAwait(false);
            }

            log.Write("service stopped");
        }

        return 0;
    }
}

internal sealed record ServiceStartupOptions
{
    public const string Usage =
        "Usage: DlnRenderer.Service [--http-port N] [--no-ssdp] [--device-name NAME] " +
        "[--listen-address IP] [--log PATH] [--exit-after SECONDS] [--no-hwdec]";

    public string FriendlyName { get; init; } = "DlnRenderer";

    /// <summary>
    /// Stable default HTTP port. A fixed port keeps the advertised LOCATION identical across
    /// restarts, which is what firewall rules, control-point caches and bookmarks depend on.
    /// Pass <c>--http-port 0</c> to fall back to an ephemeral port.
    /// </summary>
    public const int DefaultHttpPort = 18090;

    public int HttpPort { get; init; } = DefaultHttpPort;

    public bool Ssdp { get; init; } = true;

    public bool HardwareDecoding { get; init; } = true;

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
            switch (argument)
            {
                case "--no-ssdp":
                    result = result with { Ssdp = false };
                    break;

                case "--no-hwdec":
                    result = result with { HardwareDecoding = false };
                    break;

                case "--http-port":
                    if (!TryRead(args, ref i, out var portText)
                        || !int.TryParse(portText, out var port)
                        || port is < 0 or > 65535)
                    {
                        error = "--http-port requires a port number between 0 and 65535";
                        options = result;
                        return false;
                    }

                    result = result with { HttpPort = port };
                    break;

                case "--device-name":
                    if (!TryRead(args, ref i, out var name) || string.IsNullOrWhiteSpace(name))
                    {
                        error = "--device-name requires a non-empty value";
                        options = result;
                        return false;
                    }

                    result = result with { FriendlyName = name.Trim() };
                    break;

                case "--listen-address":
                    if (!TryRead(args, ref i, out var addressText)
                        || !IPAddress.TryParse(addressText, out var address))
                    {
                        error = "--listen-address requires an IPv4 address";
                        options = result;
                        return false;
                    }

                    result = result with { ListenAddress = address };
                    break;

                case "--log":
                    if (!TryRead(args, ref i, out var logPath))
                    {
                        error = "--log requires a path";
                        options = result;
                        return false;
                    }

                    result = result with { LogPath = logPath };
                    break;

                case "--exit-after":
                    if (!TryRead(args, ref i, out var secondsText)
                        || !double.TryParse(secondsText, out var seconds)
                        || seconds <= 0)
                    {
                        error = "--exit-after requires a positive number of seconds";
                        options = result;
                        return false;
                    }

                    result = result with { ExitAfter = TimeSpan.FromSeconds(seconds) };
                    break;

                case "--no-log":
                    result = result with { LogPath = null };
                    break;

                default:
                    error = $"unknown argument '{argument}'";
                    options = result;
                    return false;
            }
        }

        options = result;
        return true;
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
