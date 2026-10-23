using DlnRenderer.Core.Network;

namespace DlnRenderer.Core.Tests;

/// <summary>
/// Pins the decision logic behind the "control points cannot discover this device" warning: what
/// counts as a rule that covers us, and what does not.
/// </summary>
public class FirewallCoverageTests
{
    private static readonly string[] PrivateOnly = ["Private"];

    private static FirewallRuleEntry Rule(
        string? protocol = "UDP",
        string? localPort = "1900",
        string? program = null,
        string? profiles = "Any")
        => new() { Protocol = protocol, LocalPort = localPort, Program = program, Profiles = profiles };

    private static FirewallExposure Evaluate(
        IEnumerable<FirewallRuleEntry> rules,
        IEnumerable<string>? activeProfiles = null,
        IEnumerable<string>? enabledProfiles = null,
        int httpPort = FirewallCoverage.DefaultHttpPort)
        => FirewallCoverage.Evaluate(
            rules,
            activeProfiles ?? ["Private"],
            enabledProfiles ?? ["Private", "Public", "Domain"],
            httpPort);

    [Fact]
    public void Both_ports_covered_by_any_port_rules_is_open()
    {
        var exposure = Evaluate([Rule(protocol: "Any", localPort: "Any")]);

        Assert.True(exposure.IsKnown);
        Assert.True(exposure.IsOpen);
        Assert.False(exposure.NeedsAttention);
        Assert.Equal(FirewallPortState.Covered, exposure.Ssdp);
        Assert.Equal(FirewallPortState.Covered, exposure.Http);
    }

    [Fact]
    public void One_rule_per_protocol_and_port_is_enough()
    {
        var exposure = Evaluate([Rule("UDP", "1900"), Rule("TCP", "18090")]);

        Assert.True(exposure.IsOpen);
    }

    [Fact]
    public void Numeric_protocols_and_port_lists_are_understood()
    {
        // 17 = UDP, 6 = TCP; Windows returns numbers in some shapes and names in others.
        var exposure = Evaluate([Rule("17", "1900, 1901"), Rule("6", "18000-18099")]);

        Assert.True(exposure.IsOpen);
    }

    [Fact]
    public void A_tcp_rule_does_not_cover_ssdp()
    {
        var exposure = Evaluate([Rule("TCP", "18090")]);

        Assert.Equal(FirewallPortState.Missing, exposure.Ssdp);
        Assert.Equal(FirewallPortState.Covered, exposure.Http);
        Assert.True(exposure.NeedsAttention);
        Assert.False(exposure.IsOpen);
    }

    [Fact]
    public void Rules_scoped_to_another_program_do_not_count()
    {
        var exposure = Evaluate(
        [
            Rule("UDP", "1900", @"C:\Program Files\Other\OtherApp.exe"),
            Rule("TCP", "18090", @"C:\Program Files\Other\OtherApp.exe"),
        ]);

        Assert.Equal(FirewallPortState.Missing, exposure.Ssdp);
        Assert.Equal(FirewallPortState.Missing, exposure.Http);
        Assert.True(exposure.NeedsAttention);
    }

    [Fact]
    public void A_rule_scoped_to_the_service_binary_counts_even_from_a_versioned_package_path()
    {
        // WindowsApps\<version> changes on every upgrade, so only the file name can be matched.
        var exposure = Evaluate(
        [
            Rule("UDP", "1900", @"C:\Program Files\WindowsApps\App_1.2.3.0_x64__abc\DlnRenderer.Service.exe"),
            Rule("TCP", "18090", @"C:\Program Files\DlnRenderer\DlnRenderer.Service.exe"),
        ]);

        Assert.True(exposure.IsOpen);
    }

    [Fact]
    public void A_rule_for_the_wrong_protocol_does_not_count()
    {
        var exposure = Evaluate([Rule("UDP", "18090"), Rule("TCP", "1900")]);

        Assert.Equal(FirewallPortState.Missing, exposure.Ssdp);
        Assert.Equal(FirewallPortState.Missing, exposure.Http);
    }

    [Fact]
    public void A_rule_limited_to_another_network_category_is_reported_as_profile_blocked()
    {
        // The portable installer deliberately limits its rules to private/domain networks, so a
        // machine that answered "public network" in Windows is unreachable by design.
        var exposure = Evaluate(
            [Rule("UDP", "1900", profiles: "Private, Domain"), Rule("TCP", "18090", profiles: "Private, Domain")],
            activeProfiles: ["Public"]);

        Assert.Equal(FirewallPortState.ProfileBlocked, exposure.Ssdp);
        Assert.Equal(FirewallPortState.ProfileBlocked, exposure.Http);
        Assert.True(exposure.IsProfileRestricted);
        Assert.True(exposure.NeedsAttention);
    }

    [Fact]
    public void A_rule_that_matches_the_active_category_is_covered()
    {
        var exposure = Evaluate(
            [Rule("UDP", "1900", profiles: "Private, Domain"), Rule("TCP", "18090", profiles: "Private, Domain")],
            activeProfiles: ["Private"]);

        Assert.True(exposure.IsOpen);
    }

    [Fact]
    public void The_matching_rule_wins_even_if_another_one_is_profile_blocked()
    {
        var exposure = Evaluate(
            [Rule("UDP", "1900", profiles: "Public"), Rule("UDP", "1900", profiles: "Private")],
            activeProfiles: ["Private"]);

        Assert.Equal(FirewallPortState.Covered, exposure.Ssdp);
    }

    [Fact]
    public void Firewall_switched_off_on_the_active_network_needs_no_rules()
    {
        var exposure = Evaluate(Array.Empty<FirewallRuleEntry>(), enabledProfiles: Array.Empty<string>());

        Assert.True(exposure.IsOpen);
        Assert.False(exposure.NeedsAttention);
    }

    [Fact]
    public void An_enabled_firewall_on_the_active_profile_requires_a_rule()
    {
        var exposure = Evaluate(
            Array.Empty<FirewallRuleEntry>(),
            activeProfiles: ["Private"],
            enabledProfiles: ["Private", "Public"]);

        Assert.Equal(FirewallPortState.Missing, exposure.Ssdp);
        Assert.Equal(FirewallPortState.Missing, exposure.Http);
    }

    [Fact]
    public void No_active_network_is_not_a_problem()
    {
        var exposure = Evaluate(Array.Empty<FirewallRuleEntry>(), activeProfiles: Array.Empty<string>());

        Assert.True(exposure.IsKnown);
        Assert.False(exposure.HasActiveNetwork);
        Assert.False(exposure.NeedsAttention);
        Assert.False(exposure.IsOpen);
    }

    [Fact]
    public void An_unreadable_policy_stays_quiet()
    {
        var exposure = FirewallExposure.Unknown;

        Assert.False(exposure.IsKnown);
        Assert.False(exposure.NeedsAttention);
        Assert.False(exposure.IsOpen);
    }

    [Fact]
    public void The_http_port_is_reported_so_the_message_can_name_it()
    {
        var exposure = Evaluate([Rule("UDP", "1900")], httpPort: 18123);

        Assert.Equal(18123, exposure.HttpPort);
        Assert.Equal(FirewallPortState.Covered, exposure.Ssdp);
        Assert.Equal(FirewallPortState.Missing, exposure.Http);
    }

    [Fact]
    public void Profile_names_are_compared_case_insensitively_and_domain_authenticated_maps_to_domain()
    {
        var exposure = Evaluate(
            [Rule("UDP", "1900", profiles: "domain"), Rule("TCP", "18090", profiles: "Domain")],
            activeProfiles: ["DomainAuthenticated"]);

        Assert.True(exposure.IsOpen);
    }
}
