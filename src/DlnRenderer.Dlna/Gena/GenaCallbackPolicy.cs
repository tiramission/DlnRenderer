using System.Net;
using System.Net.Sockets;

namespace DlnRenderer.Dlna.Gena;

/// <summary>
/// Decides whether a <c>CALLBACK</c> URL may be used for eventing.
/// </summary>
/// <remarks>
/// GENA makes the device connect wherever the control point says, which is the classic shape of a
/// server-side request forgery: a subscriber could otherwise point the renderer at arbitrary hosts
/// and use it as a proxy. The policy therefore rejects anything but a plain http URL to a literal
/// address that this renderer is willing to talk to:
/// <list type="bullet">
/// <item>scheme must be <c>http</c> - no https (no trust store is configured here), and never
/// <c>file:</c>, <c>ftp:</c> or anything that would execute rather than fetch;</item>
/// <item>credentials in the URL are rejected instead of being silently forwarded;</item>
/// <item>host must be an IP literal, not a name, so no DNS lookup is performed on attacker input
/// (which is also how GENA clients behave: they announce where to reach them);</item>
/// <item>loopback, unspecified, multicast, broadcast and 169.254.x.x (cloud metadata lives there) are
/// refused, as are IPv4-mapped IPv6 forms of the same addresses;</item>
/// <item>port 0 and out-of-range ports are refused.</item>
/// </list>
/// Private ranges are allowed by default: phones on the LAN authenticate by nothing else than their
/// address, and forbidding them would break the only clients that matter.
/// </remarks>
public static class GenaCallbackPolicy
{
    /// <summary>Maximum length of a CALLBACK value; longer values are truncated by intermediaries anyway.</summary>
    public const int MaxCallbackLength = 2048;

    /// <summary>Parses and validates a CALLBACK header value.</summary>
    /// <param name="header">Raw header value, typically <c>&lt;http://192.168.1.72:4004/cb&gt;</c>.</param>
    /// <param name="options">Eventing limits and allowances.</param>
    /// <param name="callback">The validated target when the method returns true.</param>
    /// <param name="error">Reason for rejection, for logs and the HTTP error body.</param>
    public static bool TryParse(string? header, GenaOptions options, out GenaCallback callback, out string error)
    {
        callback = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(header))
        {
            error = "CALLBACK is required";
            return false;
        }

        if (header.Length > MaxCallbackLength)
        {
            error = "CALLBACK is too long";
            return false;
        }

        // The spec wraps the URL in angle brackets and allows several alternatives. Only the first
        // usable one is taken: sending two notifications for every change would duplicate events.
        var candidates = ExtractUrls(header);
        if (candidates.Count == 0)
        {
            error = "CALLBACK must be enclosed in <>";
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (TryValidateOne(candidate, options, out callback, out error))
            {
                return true;
            }
        }

        // Report the first reason: with several alternatives it is the most useful diagnostic.
        _ = TryValidateOne(candidates[0], options, out _, out error);
        return false;
    }

    /// <summary>Validates a single URL without the header-level angle brackets.</summary>
    public static bool TryValidateOne(string candidate, GenaOptions options, out GenaCallback callback, out string error)
    {
        callback = null!;
        error = string.Empty;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            error = "CALLBACK is not an absolute URL";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            // https is refused deliberately too: the renderer has no trust configuration for the
            // control point's certificate, and silently skipping verification would be worse.
            error = $"scheme '{uri.Scheme}' is not supported; only http is accepted";
            return false;
        }

        if (uri.UserInfo.Length > 0)
        {
            error = "CALLBACK must not contain credentials";
            return false;
        }

        if (!IPAddress.TryParse(uri.DnsSafeHost, out var address))
        {
            error = "CALLBACK host must be an IP address";
            return false;
        }

        if (uri.Port is < 1 or > 65535)
        {
            error = "CALLBACK port is out of range";
            return false;
        }

        if (!IsAllowedTarget(address, options, out error))
        {
            return false;
        }

        // Rebuild the URI from the validated parts so nothing from the original string (fragments,
        // unusual percent-encoding) reaches the HTTP client.
        var builder = new UriBuilder(Uri.UriSchemeHttp, address.ToString(), uri.Port)
        {
            Path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath,
            Query = uri.Query.TrimStart('?'),
        };

        callback = new GenaCallback(builder.Uri);
        return true;
    }

    /// <summary>Address allow-list, applied to the literal address (never to a resolved name).</summary>
    public static bool IsAllowedTarget(IPAddress address, GenaOptions options, out string error)
    {
        error = string.Empty;

        // IPv4-mapped IPv6 (::ffff:127.0.0.1) must be classified as the IPv4 address it wraps,
        // otherwise the loopback check below is trivially bypassed.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            error = "CALLBACK address family is not supported";
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            error = options.AllowLoopbackCallbacks
                ? string.Empty
                : "CALLBACK must not target the loopback address";
            return options.AllowLoopbackCallbacks;
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            error = "CALLBACK must not target an unspecified address";
            return false;
        }

        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // Link-local 169.254.0.0/16 hosts cloud metadata services (169.254.169.254).
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                error = "CALLBACK must not target a link-local address";
                return false;
            }

            // Multicast and the limited broadcast address are never a control point.
            if (bytes[0] is >= 224 and <= 239 || (bytes[0] == 255 && bytes[1] == 255 && bytes[2] == 255 && bytes[3] == 255))
            {
                error = "CALLBACK must not target a multicast or broadcast address";
                return false;
            }

            var isPrivate = bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);

            if (isPrivate && !options.AllowPrivateNetworkCallbacks)
            {
                error = "CALLBACK must not target a private address";
                return false;
            }

            return true;
        }

        // IPv6: refuse link-local (fe80::/10) and multicast (ff00::/8); unique-local (fc00::/7) is
        // treated like RFC1918 above.
        if ((bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) || bytes[0] == 0xff)
        {
            error = "CALLBACK must not target a link-local or multicast address";
            return false;
        }

        var isUniqueLocal = bytes[0] == 0xfc || bytes[0] == 0xfd;
        if (isUniqueLocal && !options.AllowPrivateNetworkCallbacks)
        {
            error = "CALLBACK must not target a private address";
            return false;
        }

        return true;
    }

    private static List<string> ExtractUrls(string header)
    {
        var urls = new List<string>();
        var index = 0;

        while (index < header.Length)
        {
            var start = header.IndexOf('<', index);
            if (start < 0)
            {
                break;
            }

            var end = header.IndexOf('>', start + 1);
            if (end < 0)
            {
                break;
            }

            var value = header[(start + 1)..end].Trim();
            if (value.Length > 0)
            {
                urls.Add(value);
            }

            index = end + 1;
        }

        return urls;
    }
}
