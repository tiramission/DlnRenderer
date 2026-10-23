using System.Collections;
using System.Globalization;
using System.Reflection;
using DlnRenderer.Core.Network;

namespace DlnRenderer.Services;

/// <summary>
/// Reads the local Windows Defender Firewall policy: the inbound allow rules, the network profiles
/// currently in use, and which of those profiles have the firewall switched on.
/// </summary>
/// <remarks>
/// <para>
/// Why COM rather than the <c>NetSecurity</c> PowerShell cmdlets: one rule's port filter costs a CIM
/// query, and this machine has ~700 inbound rules. Filtering by display name still took 4.7 s,
/// which is far too slow for something the window shows on open. The firewall's own automation
/// object hands out every rule's protocol, port, program and profile in one enumeration, in well
/// under a second.
/// </para>
/// <para>
/// Members are reached through <see cref="Type.InvokeMember(string, BindingFlags, Binder?, object?, object?[])"/>
/// on the type returned by <see cref="Type.GetTypeFromProgID"/>, i.e. IDispatch by name. That is
/// deliberately not hand-written interop declarations: those bind by vtable position or dispatch id,
/// both of which are easy to get subtly wrong (a wrong dispatch id fails at runtime with
/// <c>DISP_E_MEMBERNOTFOUND</c>), while the names are documented and stable.
/// </para>
/// <para>
/// Reading is allowed for a standard user; only creating rules needs elevation. A probe that fails
/// therefore reports "unknown" rather than "blocked" - guessing here would nag users whose firewall
/// is configured correctly but opaque to us.
/// </para>
/// </remarks>
internal static class FirewallPolicyReader
{
    private const string PolicyProgId = "HNetCfg.FwPolicy2";

    private const int ProtocolTcp = 6;
    private const int ProtocolUdp = 17;
    private const int ProtocolAny = 256;

    private const int DirectionInbound = 1;
    private const int ActionAllow = 1;

    /// <summary>NET_FW_PROFILE2_*: the same bits describe the current profile and each rule's scope.</summary>
    private static readonly (int Flag, string Name)[] Profiles =
    [
        (1, "Domain"),
        (2, "Private"),
        (4, "Public"),
    ];

    /// <summary>Reads the policy. Throws when the firewall API itself is unavailable.</summary>
    public static FirewallPolicySnapshot Read()
    {
        var policyType = Type.GetTypeFromProgID(PolicyProgId)
            ?? throw new PlatformNotSupportedException($"Windows Firewall policy ({PolicyProgId}) is not registered.");
        var policy = Activator.CreateInstance(policyType)
            ?? throw new InvalidOperationException($"Could not create the Windows Firewall policy object ({PolicyProgId}).");

        var current = Convert.ToInt32(GetProperty(policy, "CurrentProfileTypes"), CultureInfo.InvariantCulture);
        var active = new List<string>();
        var enabled = new List<string>();

        foreach (var (flag, name) in Profiles)
        {
            if ((current & flag) == 0)
            {
                continue;
            }

            active.Add(name);

            // Parameterised property: COM get_FirewallEnabled(profileType).
            var isOn = policyType.InvokeMember(
                "FirewallEnabled",
                BindingFlags.GetProperty,
                binder: null,
                target: policy,
                args: [flag]);
            if (isOn is true)
            {
                enabled.Add(name);
            }
        }

        var rules = new List<FirewallRuleEntry>();
        if (GetProperty(policy, "Rules") is IEnumerable enumerable)
        {
            foreach (var rule in enumerable)
            {
                if (rule is null
                    || GetProperty(rule, "Enabled") is not true
                    || ReadInt(rule, "Direction") != DirectionInbound
                    || ReadInt(rule, "Action") != ActionAllow)
                {
                    continue;
                }

                rules.Add(new FirewallRuleEntry
                {
                    Protocol = DescribeProtocol(ReadInt(rule, "Protocol")),
                    LocalPort = DescribePorts(GetProperty(rule, "LocalPorts") as string),
                    Program = GetProperty(rule, "ApplicationName") as string,
                    Profiles = DescribeProfiles(ReadInt(rule, "Profiles")),
                });
            }
        }

        return new FirewallPolicySnapshot(rules, active, enabled);
    }

    private static object? GetProperty(object target, string name)
        => target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    private static int ReadInt(object target, string name)
        => GetProperty(target, name) is { } value
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : 0;

    private static string DescribeProtocol(int protocol) => protocol switch
    {
        ProtocolTcp => "TCP",
        ProtocolUdp => "UDP",
        ProtocolAny => "Any",
        _ => protocol.ToString(CultureInfo.InvariantCulture),
    };

    private static string DescribePorts(string? localPorts)
        => string.IsNullOrWhiteSpace(localPorts) ? "Any" : localPorts;

    private static string DescribeProfiles(int profiles)
    {
        // NET_FW_PROFILE2_ALL = 0x7FFFFFFF: covers every network, current and future.
        if (profiles == 0x7FFFFFFF)
        {
            return "Any";
        }

        var names = Profiles.Where(entry => (profiles & entry.Flag) != 0).Select(entry => entry.Name).ToArray();
        return names.Length == 0 ? "Any" : string.Join(", ", names);
    }
}

/// <summary>A snapshot of the firewall policy, reduced to what coverage evaluation needs.</summary>
internal sealed record FirewallPolicySnapshot(
    IReadOnlyList<FirewallRuleEntry> InboundAllowRules,
    IReadOnlyList<string> ActiveProfiles,
    IReadOnlyList<string> FirewallEnabledProfiles);
