using DlnRenderer.Core.Network;
using Microsoft.UI.Dispatching;

namespace DlnRenderer.Services;

/// <summary>
/// Shared view of whether the Windows firewall lets control points reach this renderer, plus the
/// action that fixes it.
/// </summary>
/// <remarks>
/// <para>
/// Owned by <see cref="App"/> because two pages show the same fact: the player page warns that a
/// cast may never arrive, and the settings page explains and repairs it. One probe and one event
/// keep them from disagreeing.
/// </para>
/// <para>
/// The probe runs off the UI thread (COM enumeration of ~1000 rules takes tens of milliseconds, not
/// seconds, but it must still never block a frame) and publishes back through the dispatcher.
/// </para>
/// </remarks>
public sealed class FirewallMonitor
{
    private readonly DispatcherQueue _dispatcher;
    private readonly object _gate = new();
    private Task? _probe;
    private int _httpPort = FirewallCoverage.DefaultHttpPort;

    public FirewallMonitor(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Exposure = FirewallExposure.Unknown;
    }

    /// <summary>Raised on the UI thread when <see cref="Exposure"/> or <see cref="IsProbing"/> changed.</summary>
    public event EventHandler? Changed;

    public FirewallExposure Exposure { get; private set; }

    public bool IsProbing { get; private set; }

    /// <summary>
    /// Port the service advertises, taken from its control URL once it reports one. Until then the
    /// documented default is used, which is also what the firewall scripts assume.
    /// </summary>
    public int HttpPort
    {
        get => Volatile.Read(ref _httpPort);
        set
        {
            if (value is > 0 and <= 65535)
            {
                Volatile.Write(ref _httpPort, value);
            }
        }
    }

    /// <summary>
    /// Probes the policy, reusing an in-flight probe. Safe to call from a page's Loaded handler and
    /// from every snapshot: the result is the same question.
    /// </summary>
    public Task RefreshAsync()
    {
        lock (_gate)
        {
            if (_probe is { IsCompleted: false })
            {
                return _probe;
            }

            // Queued rather than started inline: the task is stored before any handler can observe
            // "probing", so a handler that asks again joins this probe instead of starting another.
            _probe = Task.Run(ProbeAsync);
            return _probe;
        }
    }

    /// <summary>
    /// Adds the inbound rules through one UAC prompt and re-probes. Returns a message to show either
    /// way: a cancelled prompt must not look like success.
    /// </summary>
    public async Task<(bool Succeeded, string Message)> AllowDiscoveryAsync()
    {
        var (installed, error) = await FirewallRuleInstaller
            .InstallAsync(HttpPort, ServiceClient.ResolveServicePath(), PackageFamilyName())
            .ConfigureAwait(true);

        if (!installed)
        {
            return (false, error ?? "防火墙规则未添加。");
        }

        await RefreshAsync().ConfigureAwait(true);
        return Exposure.IsOpen
            ? (true, "已放行局域网发现：控制端现在可以发现并投送媒体。")
            : (false, "规则已添加，但检查仍显示未放行。请确认当前网络未被 Windows 标记为“公用”。");
    }

    private async Task ProbeAsync()
    {
        Post(() =>
        {
            IsProbing = true;
            Changed?.Invoke(this, EventArgs.Empty);
        });

        var port = HttpPort;
        var exposure = await Task.Run(() =>
        {
            try
            {
                var snapshot = FirewallPolicyReader.Read();
                return FirewallCoverage.Evaluate(
                    snapshot.InboundAllowRules,
                    snapshot.ActiveProfiles,
                    snapshot.FirewallEnabledProfiles,
                    port);
            }
            catch (Exception ex)
            {
                // Unknown, not blocked: a policy we cannot read must not produce a warning that
                // tells users their working setup is broken.
                DiagnosticsLog.Write($"firewall probe failed: {ex.Message}");
                return FirewallExposure.Unknown;
            }
        }).ConfigureAwait(true);

        Post(() =>
        {
            Exposure = exposure;
            IsProbing = false;
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Post(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }

    private static string? PackageFamilyName()
    {
        if (!StartupRegistration.IsPackaged)
        {
            return null;
        }

        try
        {
            return Windows.ApplicationModel.Package.Current.Id.FamilyName;
        }
        catch (Exception ex)
        {
            // Without identity the rule falls back to the executable path, which still works until
            // the package directory changes.
            DiagnosticsLog.Write($"package family name unavailable: {ex.Message}");
            return null;
        }
    }
}
