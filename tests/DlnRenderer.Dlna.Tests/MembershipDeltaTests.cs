using System.Net;
using DlnRenderer.Dlna.Ssdp;

namespace DlnRenderer.Dlna.Tests;

/// <summary>
/// Multicast membership diffing, extracted from <see cref="SsdpServer"/> so the rules are testable
/// without a real adapter.
/// </summary>
/// <remarks>
/// Changing adapters on a build agent, or even on a developer machine, needs administrator rights and
/// is not reproducible in CI. The decision "which interfaces to join and leave" is therefore pure and
/// tested here; only the socket calls that consume it remain untestable.
/// </remarks>
public class MembershipDeltaTests
{
    private static IPAddress Ip(string value) => IPAddress.Parse(value);

    [Fact]
    public void Identical_sets_need_no_changes()
    {
        var delta = MembershipDelta.Compute(
            [Ip("192.168.1.9"), Ip("172.25.96.1")],
            [Ip("192.168.1.9"), Ip("172.25.96.1")]);

        Assert.False(delta.HasChanges);
        Assert.Empty(delta.Added);
        Assert.Empty(delta.Removed);
    }

    [Fact]
    public void A_new_interface_is_joined()
    {
        // Wi-Fi comes up while Ethernet is already joined.
        var delta = MembershipDelta.Compute(
            [Ip("192.168.1.9")],
            [Ip("192.168.1.9"), Ip("10.0.0.5")]);

        Assert.True(delta.HasChanges);
        Assert.Equal([Ip("10.0.0.5")], delta.Added);
        Assert.Empty(delta.Removed);
    }

    [Fact]
    public void A_disappeared_interface_is_left()
    {
        // The cable was unplugged.
        var delta = MembershipDelta.Compute(
            [Ip("192.168.1.9"), Ip("10.0.0.5")],
            [Ip("10.0.0.5")]);

        Assert.True(delta.HasChanges);
        Assert.Empty(delta.Added);
        Assert.Equal([Ip("192.168.1.9")], delta.Removed);
    }

    [Fact]
    public void A_swapped_address_is_both_left_and_joined()
    {
        // DHCP handed out a different address on the same adapter.
        var delta = MembershipDelta.Compute(
            [Ip("192.168.1.9")],
            [Ip("192.168.1.42")]);

        Assert.True(delta.HasChanges);
        Assert.Equal([Ip("192.168.1.42")], delta.Added);
        Assert.Equal([Ip("192.168.1.9")], delta.Removed);
    }

    [Fact]
    public void Losing_every_interface_removes_all_memberships()
    {
        // All adapters down: nothing to advertise on, but the server keeps running so it can
        // recover when an interface returns.
        var delta = MembershipDelta.Compute(
            [Ip("192.168.1.9"), Ip("172.25.96.1")],
            []);

        Assert.True(delta.HasChanges);
        Assert.Empty(delta.Added);
        Assert.Equal(2, delta.Removed.Count);
    }

    [Fact]
    public void Gaining_every_interface_joins_all()
    {
        var delta = MembershipDelta.Compute([], [Ip("192.168.1.9"), Ip("172.25.96.1")]);

        Assert.True(delta.HasChanges);
        Assert.Equal(2, delta.Added.Count);
        Assert.Empty(delta.Removed);
    }

    [Fact]
    public void Nothing_joined_and_nothing_present_is_a_no_op()
    {
        var delta = MembershipDelta.Compute([], []);

        Assert.False(delta.HasChanges);
    }

    [Fact]
    public void Applying_the_delta_makes_the_sets_match()
    {
        // The property the server relies on: after join/leave, joined == current.
        var joined = new List<IPAddress> { Ip("192.168.1.9"), Ip("172.25.96.1") };
        var current = new List<IPAddress> { Ip("172.25.96.1"), Ip("10.0.0.5") };

        var delta = MembershipDelta.Compute(joined, current);
        joined.RemoveAll(address => delta.Removed.Contains(address));
        joined.AddRange(delta.Added);

        Assert.Equal(current.OrderBy(a => a.ToString()), joined.OrderBy(a => a.ToString()));

        // And a second pass is a no-op, which is what makes a burst of events harmless.
        Assert.False(MembershipDelta.Compute(joined, current).HasChanges);
    }

    [Fact]
    public void Repeated_computation_is_idempotent()
    {
        IPAddress[] joined = [Ip("192.168.1.9")];
        IPAddress[] current = [Ip("192.168.1.9")];

        for (var i = 0; i < 5; i++)
        {
            Assert.False(MembershipDelta.Compute(joined, current).HasChanges);
        }
    }
}
