using System.Globalization;
using System.Text;

namespace DlnRenderer.Dlna.Ssdp;

public static class SsdpConstants
{
    public const string MulticastAddress = "239.255.255.250";
    public const int MulticastPort = 1900;
    public const string SearchAll = "ssdp:all";
    public const string RootDevice = "upnp:rootdevice";
    public const string MediaRendererDeviceType = "urn:schemas-upnp-org:device:MediaRenderer:1";

    // Service-level targets. Several control points discover renderers by service type rather than
    // by device type, so these must be answered as well.
    public const string AvTransportServiceType = "urn:schemas-upnp-org:service:AVTransport:1";
    public const string RenderingControlServiceType = "urn:schemas-upnp-org:service:RenderingControl:1";
    public const string ConnectionManagerServiceType = "urn:schemas-upnp-org:service:ConnectionManager:1";
}

public sealed record SsdpSearchRequest(string SearchTarget, int Mx);

/// <summary>Parsing and construction of SSDP datagrams. Pure functions so they can be unit tested.</summary>
public static class SsdpMessage
{
    public static bool TryParseSearch(string? payload, out SsdpSearchRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        var lines = payload.Split(["\r\n", "\n"], StringSplitOptions.None);
        if (!lines[0].StartsWith("M-SEARCH ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var headers = ParseHeaders(lines);
        if (!headers.TryGetValue("man", out var man)
            || !man.Contains("ssdp:discover", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!headers.TryGetValue("st", out var st) || string.IsNullOrWhiteSpace(st))
        {
            return false;
        }

        var mx = 1;
        if (headers.TryGetValue("mx", out var mxText)
            && int.TryParse(mxText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            // MX is a delay hint in seconds; clamp so a hostile value cannot pin a task open.
            mx = Math.Clamp(parsed, 1, 5);
        }

        request = new SsdpSearchRequest(st.Trim(), mx);
        return true;
    }

    /// <summary>True when this renderer must answer the given search target.</summary>
    public static bool Matches(string searchTarget, Guid uuid)
        => TargetsForSearch(searchTarget, uuid).Count > 0;

    /// <summary>Every (ST, USN) pair this device answers a search for.</summary>
    /// <remarks>
    /// Includes the service types, not only the device type. Client implementations differ: some
    /// search for <c>MediaRenderer:1</c>, others (notably BubbleUPnP) search for
    /// <c>service:AVTransport:1</c> and <c>service:RenderingControl:1</c> to find renderers. Answering
    /// only device-level targets makes the renderer invisible to the latter group.
    /// </remarks>
    public static IReadOnlyList<(string SearchTarget, string Usn)> AdvertisedTargets(Guid uuid)
    {
        var udn = $"uuid:{uuid:D}";
        return
        [
            (SsdpConstants.RootDevice, $"{udn}::upnp:rootdevice"),
            (udn, udn),
            (SsdpConstants.MediaRendererDeviceType, $"{udn}::{SsdpConstants.MediaRendererDeviceType}"),
            (SsdpConstants.AvTransportServiceType, $"{udn}::{SsdpConstants.AvTransportServiceType}"),
            (SsdpConstants.RenderingControlServiceType, $"{udn}::{SsdpConstants.RenderingControlServiceType}"),
            (SsdpConstants.ConnectionManagerServiceType, $"{udn}::{SsdpConstants.ConnectionManagerServiceType}"),
        ];
    }

    /// <summary>
    /// The targets to answer a specific search with.
    /// </summary>
    /// <remarks>
    /// A reply's ST must equal the ST that was searched for (SSDP requires this); only
    /// <c>ssdp:all</c> expects one reply per advertised target. Answering a targeted search with
    /// every target - which this server used to do - makes strict clients treat the replies as
    /// malformed and drop the device, while looking perfectly correct in a hand-rolled client.
    /// </remarks>
    public static IReadOnlyList<(string SearchTarget, string Usn)> TargetsForSearch(string searchTarget, Guid uuid)
    {
        var st = searchTarget.Trim();
        var advertised = AdvertisedTargets(uuid);

        if (st.Equals(SsdpConstants.SearchAll, StringComparison.OrdinalIgnoreCase))
        {
            return advertised;
        }

        // Echo the exact target the control point asked for, using its own casing.
        foreach (var (target, usn) in advertised)
        {
            if (string.Equals(target, st, StringComparison.OrdinalIgnoreCase))
            {
                return [(st, usn)];
            }
        }

        return [];
    }

    public static string BuildSearchResponse(
        string searchTarget,
        string usn,
        string location,
        string server,
        TimeSpan maxAge)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 200 OK\r\n");
        builder.Append("CACHE-CONTROL: ").Append("max-age=").Append((int)maxAge.TotalSeconds).Append("\r\n");
        builder.Append("DATE: ").Append(DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
        builder.Append("EXT:\r\n");
        builder.Append("LOCATION: ").Append(location).Append("\r\n");
        builder.Append("SERVER: ").Append(server).Append("\r\n");
        builder.Append("ST: ").Append(searchTarget).Append("\r\n");
        builder.Append("USN: ").Append(usn).Append("\r\n");
        builder.Append("\r\n");
        return builder.ToString();
    }

    public static string BuildNotify(
        string notificationType,
        bool alive,
        string usn,
        string location,
        string server,
        TimeSpan maxAge)
    {
        var builder = new StringBuilder();
        builder.Append("NOTIFY * HTTP/1.1\r\n");
        builder.Append("HOST: ").Append(SsdpConstants.MulticastAddress).Append(':').Append(SsdpConstants.MulticastPort).Append("\r\n");
        if (alive)
        {
            builder.Append("CACHE-CONTROL: ").Append("max-age=").Append((int)maxAge.TotalSeconds).Append("\r\n");
            builder.Append("LOCATION: ").Append(location).Append("\r\n");
            builder.Append("SERVER: ").Append(server).Append("\r\n");
        }

        builder.Append("NT: ").Append(notificationType).Append("\r\n");
        builder.Append("NTS: ").Append(alive ? "ssdp:alive" : "ssdp:byebye").Append("\r\n");
        builder.Append("USN: ").Append(usn).Append("\r\n");
        builder.Append("\r\n");
        return builder.ToString();
    }

    private static Dictionary<string, string> ParseHeaders(string[] lines)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                break;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            headers[name] = value;
        }

        return headers;
    }
}

public static class NetworkInterfaces
{
    /// <summary>Candidate IPv4 addresses for SSDP. Loopback is excluded unless explicitly requested.</summary>
    public static IReadOnlyList<System.Net.IPAddress> GetLanAddresses(System.Net.IPAddress? preferred = null)
    {
        if (preferred is not null)
        {
            return [preferred];
        }

        // Best candidate first. On machines with VPN/tunnel adapters (for example the Windows
        // "198.18.0.0/15" tunnel range) the OS order is arbitrary; advertising such an address
        // makes the renderer undiscoverable for real control points on the LAN.
        return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .Where(n => !IsVirtualAdapter(n))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Where(a => !System.Net.IPAddress.IsLoopback(a.Address))
                .Select(a => new
                {
                    a.Address,
                    // A default gateway is the strongest signal that this address belongs to the
                    // network the user's other devices are on. Hyper-V/WSL switches are "Ethernet"
                    // by type and have no gateway, so type alone cannot rank them out.
                    HasGateway = HasDefaultGateway(n),
                    Score = ScoreAdapter(n),
                }))
            .OrderByDescending(x => x.HasGateway)
            .ThenByDescending(x => x.Score)
            .Select(x => x.Address)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Picks the local address that shares a subnet with <paramref name="remote"/>, so an answer is
    /// sent from - and advertises - the interface the requester can actually reach.
    /// Returns null when no candidate shares the requester's subnet.
    /// </summary>
    public static System.Net.IPAddress? SelectSourceAddressFor(
        System.Net.IPAddress remote,
        IReadOnlyList<System.Net.IPAddress> candidates)
    {
        if (remote.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return null;
        }

        var remoteBytes = remote.GetAddressBytes();

        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    continue;
                }

                if (unicast.PrefixLength is < 1 or > 32)
                {
                    continue;
                }

                if (!candidates.Any(c => c.Equals(unicast.Address)))
                {
                    continue;
                }

                if (InSameSubnet(remoteBytes, unicast.Address.GetAddressBytes(), unicast.PrefixLength))
                {
                    return unicast.Address;
                }
            }
        }

        return null;
    }

    private static bool InSameSubnet(byte[] a, byte[] b, int prefixLength)
    {
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (a[fullBytes] & mask) == (b[fullBytes] & mask);
    }

    private static bool HasDefaultGateway(System.Net.NetworkInformation.NetworkInterface nic)
        => nic.GetIPProperties().GatewayAddresses
            .Any(g => g.Address is not null
                      && !g.Address.Equals(System.Net.IPAddress.Any)
                      && !g.Address.Equals(System.Net.IPAddress.IPv6Any));

    /// <summary>Higher is better: a real Ethernet/Wi-Fi link beats anything exotic.</summary>
    private static int ScoreAdapter(System.Net.NetworkInformation.NetworkInterface nic)
        => nic.NetworkInterfaceType switch
        {
            System.Net.NetworkInformation.NetworkInterfaceType.Ethernet => 3,
            System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 => 3,
            System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet => 3,
            System.Net.NetworkInformation.NetworkInterfaceType.FastEthernetT => 3,
            System.Net.NetworkInformation.NetworkInterfaceType.Tunnel => 0,
            _ => 1,
        };

    /// <summary>
    /// Filters adapters that cannot serve DLNA control points: loopback, tunnels (including the
    /// Windows 198.18.0.0/15 VPN/tunnel range) and adapters without a usable prefix.
    /// </summary>
    private static bool IsVirtualAdapter(System.Net.NetworkInformation.NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
        {
            return true;
        }

        foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
        {
            if (unicast.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                continue;
            }

            var bytes = unicast.Address.GetAddressBytes();

            // 198.18.0.0/15 is reserved for benchmarking but is widely reused by VPN clients
            // (including the one on this machine); it is never a reachable LAN address.
            if (bytes[0] == 198 && bytes[1] is 18 or 19)
            {
                return true;
            }

            // 169.254.0.0/16 means DHCP failed; nothing can reach the device there.
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }
        }

        return false;
    }
}
