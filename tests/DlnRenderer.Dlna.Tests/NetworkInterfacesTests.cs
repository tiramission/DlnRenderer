using System.Net;
using System.Net.NetworkInformation;
using DlnRenderer.Dlna.Ssdp;

namespace DlnRenderer.Dlna.Tests;

public class NetworkInterfacesTests
{
    [Fact]
    public void An_explicit_address_wins_over_discovery()
    {
        var explicitAddress = IPAddress.Parse("127.0.0.1");

        Assert.Equal([explicitAddress], NetworkInterfaces.GetLanAddresses(explicitAddress));
    }

    [Fact]
    public void Discovered_addresses_are_usable_ipv4_lan_addresses()
    {
        var addresses = NetworkInterfaces.GetLanAddresses();

        Assert.All(addresses, address =>
        {
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, address.AddressFamily);
            Assert.False(IPAddress.IsLoopback(address));

            var bytes = address.GetAddressBytes();

            // 198.18.0.0/15 is used by VPN/tunnel adapters and is unreachable from the LAN; the
            // renderer must never advertise it. 169.254.0.0/16 means DHCP failed.
            Assert.False(bytes[0] == 198 && bytes[1] is 18 or 19, $"{address} is a tunnel address");
            Assert.False(bytes[0] == 169 && bytes[1] == 254, $"{address} is a link-local address");
        });
    }

    [Fact]
    public void Discovery_is_bounded_by_the_machines_real_adapters()
    {
        // Every returned address must belong to an adapter that is actually up, so a stale
        // address can never be advertised.
        var upAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .ToHashSet();

        Assert.All(NetworkInterfaces.GetLanAddresses(), address => Assert.Contains(address, upAddresses));
    }
}
