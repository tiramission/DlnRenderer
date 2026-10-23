namespace DlnRenderer.Core.Network;

/// <summary>What the machine's firewall says about one inbound port the renderer depends on.</summary>
public enum FirewallPortState
{
    /// <summary>Nothing to decide: no active network, firewall off, or the policy could not be read.</summary>
    Unknown,

    /// <summary>An enabled inbound allow rule admits traffic on this port from the current network.</summary>
    Covered,

    /// <summary>No rule admits it: control points cannot reach the renderer.</summary>
    Missing,

    /// <summary>
    /// A rule admits the port, but only for network profiles this machine is not currently on
    /// (typically a "private/domain only" rule while the network is "public").
    /// </summary>
    ProfileBlocked,
}

/// <summary>One inbound, enabled, allow rule reduced to the fields that decide coverage.</summary>
/// <remarks>
/// Everything is a string because it comes from Windows in three different shapes depending on how
/// it is read (COM gives numbers and nulls, PowerShell gives names); normalisation lives in
/// <see cref="FirewallCoverage"/> so the caller only has to report what Windows said.
/// </remarks>
public sealed record FirewallRuleEntry
{
    /// <summary><c>TCP</c>, <c>UDP</c>, <c>Any</c>, or the numeric protocol (6, 17, 256).</summary>
    public string? Protocol { get; init; }

    /// <summary><c>Any</c>, <c>1900</c>, <c>18090,18091</c> or a range such as <c>18090-18099</c>.</summary>
    public string? LocalPort { get; init; }

    /// <summary>Full path of the allowed executable, or null/<c>Any</c> when not scoped to one.</summary>
    public string? Program { get; init; }

    /// <summary><c>Any</c>, or a list such as <c>Domain, Private, Public</c>.</summary>
    public string? Profiles { get; init; }
}

/// <summary>
/// Whether the Windows firewall currently lets a control point reach this renderer.
/// </summary>
/// <remarks>
/// Without an inbound rule for SSDP the device is never discovered, and without one for the HTTP
/// port a control point that somehow knows the address cannot even fetch the description - from the
/// user's side both look like "the app is broken". The two ports are therefore reported separately.
/// </remarks>
public sealed record FirewallExposure
{
    /// <summary>Nothing was probed, or the probe failed. The UI stays quiet.</summary>
    public static FirewallExposure Unknown { get; } = new() { IsKnown = false };

    /// <summary>False when the policy could not be read; unknown is not "blocked".</summary>
    public bool IsKnown { get; init; }

    /// <summary>False when no network interface is connected: nothing to be blocked from.</summary>
    public bool HasActiveNetwork { get; init; } = true;

    /// <summary>TCP port the description/SOAP/GENA endpoint listens on.</summary>
    public int HttpPort { get; init; }

    public FirewallPortState Ssdp { get; init; } = FirewallPortState.Unknown;

    public FirewallPortState Http { get; init; } = FirewallPortState.Unknown;

    /// <summary>True when a control point can both discover the device and talk to it.</summary>
    public bool IsOpen => IsKnown
        && HasActiveNetwork
        && Ssdp == FirewallPortState.Covered
        && Http == FirewallPortState.Covered;

    /// <summary>True when the UI has something actionable to say.</summary>
    public bool NeedsAttention => IsKnown
        && HasActiveNetwork
        && (Ssdp is FirewallPortState.Missing or FirewallPortState.ProfileBlocked
            || Http is FirewallPortState.Missing or FirewallPortState.ProfileBlocked);

    /// <summary>True when rules exist but the current network category is not one of their profiles.</summary>
    public bool IsProfileRestricted =>
        IsKnown && HasActiveNetwork
        && (Ssdp == FirewallPortState.ProfileBlocked || Http == FirewallPortState.ProfileBlocked);
}

/// <summary>Decides firewall coverage from a snapshot of rules and network state. Pure, no IO.</summary>
public static class FirewallCoverage
{
    /// <summary>SSDP: discovery and alive/byebye announcements.</summary>
    public const int SsdpPort = 1900;

    /// <summary>
    /// Default HTTP port of the service, mirrored from <c>ServiceStartupOptions.DefaultHttpPort</c>
    /// so the UI can explain itself before it has ever talked to a service.
    /// </summary>
    public const int DefaultHttpPort = 18090;

    /// <summary>
    /// The rule is scoped to the service executable. Matched by file name rather than by full path
    /// because the packaged build lives under <c>WindowsApps\&lt;version&gt;</c>, whose path changes
    /// with every upgrade.
    /// </summary>
    public const string ServiceExecutableName = "DlnRenderer.Service.exe";

    /// <summary>
    /// Evaluates coverage.
    /// </summary>
    /// <param name="rules">Inbound, enabled, allow rules (filtering those is the caller's job).</param>
    /// <param name="activeProfiles">Network categories currently in use: Domain, Private, Public.</param>
    /// <param name="firewallEnabledProfiles">Profiles for which the firewall is switched on.</param>
    /// <param name="httpPort">Port the description/SOAP/GENA endpoint listens on.</param>
    /// <param name="serviceExecutableName">File name used to tell our rules from other programs'.</param>
    public static FirewallExposure Evaluate(
        IEnumerable<FirewallRuleEntry> rules,
        IEnumerable<string> activeProfiles,
        IEnumerable<string> firewallEnabledProfiles,
        int httpPort = DefaultHttpPort,
        string serviceExecutableName = ServiceExecutableName)
    {
        var active = NormalizeProfiles(activeProfiles);
        if (active.Count == 0)
        {
            return new FirewallExposure { IsKnown = true, HasActiveNetwork = false, HttpPort = httpPort };
        }

        var enabled = NormalizeProfiles(firewallEnabledProfiles);
        var blocking = active.Where(enabled.Contains).ToArray();
        if (blocking.Length == 0)
        {
            // The firewall is off on every connected network: no rule is needed.
            return new FirewallExposure
            {
                IsKnown = true,
                HttpPort = httpPort,
                Ssdp = FirewallPortState.Covered,
                Http = FirewallPortState.Covered,
            };
        }

        var entries = rules as IReadOnlyCollection<FirewallRuleEntry> ?? rules.ToArray();

        return new FirewallExposure
        {
            IsKnown = true,
            HasActiveNetwork = true,
            HttpPort = httpPort,
            Ssdp = EvaluatePort(entries, "UDP", SsdpPort, blocking, serviceExecutableName),
            Http = EvaluatePort(entries, "TCP", httpPort, blocking, serviceExecutableName),
        };
    }

    private static FirewallPortState EvaluatePort(
        IReadOnlyCollection<FirewallRuleEntry> rules,
        string protocol,
        int port,
        IReadOnlyCollection<string> blockingProfiles,
        string serviceExecutableName)
    {
        var profileBlocked = false;

        foreach (var rule in rules)
        {
            if (!CoversProgram(rule.Program, serviceExecutableName)
                || !CoversProtocol(rule.Protocol, protocol)
                || !CoversPort(rule.LocalPort, port))
            {
                continue;
            }

            if (CoversProfiles(rule.Profiles, blockingProfiles))
            {
                return FirewallPortState.Covered;
            }

            // A rule for the right port that does not apply to this network: worth telling the user
            // apart from "no rule at all", because the fix is different.
            profileBlocked = true;
        }

        return profileBlocked ? FirewallPortState.ProfileBlocked : FirewallPortState.Missing;
    }

    /// <summary>
    /// True when the rule applies to our service: unscoped rules always do, and path-scoped ones only
    /// when they name our executable.
    /// </summary>
    private static bool CoversProgram(string? program, string serviceExecutableName)
    {
        if (string.IsNullOrWhiteSpace(program)
            || program.Equals("Any", StringComparison.OrdinalIgnoreCase)
            || program == "*")
        {
            return true;
        }

        var fileName = Path.GetFileName(program.Trim());
        return fileName.Equals(serviceExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Normalises <c>TCP</c>/<c>UDP</c>/<c>Any</c> and their numeric protocol numbers.</summary>
    private static bool CoversProtocol(string? ruleProtocol, string protocol)
    {
        var value = ruleProtocol?.Trim();
        if (string.IsNullOrEmpty(value) || value == "*")
        {
            return true;
        }

        var normalized = value.ToUpperInvariant() switch
        {
            "6" => "TCP",
            "17" => "UDP",
            "256" => "ANY",
            "0" => "ANY",
            _ => value.ToUpperInvariant(),
        };

        return normalized == "ANY" || normalized == protocol;
    }

    /// <summary>Port list or range matching; an unscoped rule (<c>Any</c>/null) covers every port.</summary>
    private static bool CoversPort(string? localPorts, int port)
    {
        if (string.IsNullOrWhiteSpace(localPorts)
            || localPorts.Equals("Any", StringComparison.OrdinalIgnoreCase)
            || localPorts == "*")
        {
            return true;
        }

        foreach (var part in localPorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0)
            {
                if (int.TryParse(part, out var single) && single == port)
                {
                    return true;
                }

                continue;
            }

            if (int.TryParse(part[..dash], out var from)
                && int.TryParse(part[(dash + 1)..], out var to)
                && port >= Math.Min(from, to)
                && port <= Math.Max(from, to))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the rule's profiles overlap the connected networks whose firewall is on.
    /// An unscoped rule applies everywhere.
    /// </summary>
    private static bool CoversProfiles(string? ruleProfiles, IReadOnlyCollection<string> blockingProfiles)
    {
        var profiles = NormalizeProfiles(SplitProfiles(ruleProfiles));
        if (profiles.Count == 0 || profiles.Contains("All"))
        {
            return true;
        }

        return profiles.Overlaps(blockingProfiles);
    }

    private static string[] SplitProfiles(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Canonicalises profile names; unrecognised values are kept so they simply never match.</summary>
    private static HashSet<string> NormalizeProfiles(IEnumerable<string> profiles)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            foreach (var part in SplitProfiles(profile))
            {
                result.Add(part.ToUpperInvariant() switch
                {
                    "DOMAIN" or "DOMAINAUTHENTICATED" => "Domain",
                    "PRIVATE" => "Private",
                    "PUBLIC" => "Public",
                    "ALL" or "ANY" => "All",
                    _ => part,
                });
            }
        }

        return result;
    }
}
