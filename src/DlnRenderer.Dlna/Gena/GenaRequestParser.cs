using System.Globalization;
using DlnRenderer.Dlna.Http;

namespace DlnRenderer.Dlna.Gena;

/// <summary>
/// A parsed and validated SUBSCRIBE request.
/// </summary>
/// <param name="IsRenewal">True when the request carried a SID and therefore renews an existing subscription.</param>
public sealed record GenaSubscribeRequest(bool IsRenewal, string? Sid, GenaCallback? Callback, TimeSpan Timeout);

/// <summary>
/// Parses SUBSCRIBE / UNSUBSCRIBE headers according to the UPnP GENA rules.
/// </summary>
/// <remarks>
/// The correctness of the status codes matters more than it looks: control points branch on them.
/// A renewal must be answered <c>412</c> when the SID is unknown, and <c>400</c> when the request
/// mixes a SID with CALLBACK/NT, so a client can distinguish "resubscribe" from "you sent nonsense".
/// </remarks>
public static class GenaRequestParser
{
    /// <summary>Parses a SUBSCRIBE request.</summary>
    public static GenaResult ParseSubscribe(DlnHttpRequest request, GenaOptions options, out GenaSubscribeRequest? subscribe)
    {
        subscribe = null;

        // The spec's "Incompatible header fields": SID belongs to renewal, NT/CALLBACK to creation.
        var hasSid = Header(request, "SID") is { Length: > 0 };
        var hasCallback = Header(request, "CALLBACK") is { Length: > 0 };
        var hasNt = Header(request, "NT") is { Length: > 0 };

        if (hasSid && (hasCallback || hasNt))
        {
            return GenaResult.BadRequest("SID must not be combined with CALLBACK or NT");
        }

        if (!TryParseTimeout(request, options, out var timeout, out var timeoutError))
        {
            return GenaResult.BadRequest(timeoutError);
        }

        if (hasSid)
        {
            var sid = Header(request, "SID")!;
            if (!IsWellFormedSid(sid))
            {
                return GenaResult.PreconditionFailed("SID is not a valid subscription identifier");
            }

            subscribe = new GenaSubscribeRequest(true, sid, null, timeout);
            return GenaResult.Ok(new Dictionary<string, string>());
        }

        if (!hasNt)
        {
            return GenaResult.PreconditionFailed("NT must be 'upnp:event'");
        }

        var nt = Header(request, "NT")!;
        if (!string.Equals(nt, "upnp:event", StringComparison.OrdinalIgnoreCase))
        {
            return GenaResult.PreconditionFailed("NT must be 'upnp:event'");
        }

        if (!hasCallback)
        {
            return GenaResult.PreconditionFailed("CALLBACK is required for a new subscription");
        }

        if (!GenaCallbackPolicy.TryParse(Header(request, "CALLBACK"), options, out var callback, out var callbackError))
        {
            return GenaResult.PreconditionFailed(callbackError);
        }

        subscribe = new GenaSubscribeRequest(false, null, callback, timeout);
        return GenaResult.Ok(new Dictionary<string, string>());
    }

    /// <summary>Parses an UNSUBSCRIBE request. Returns the SID to remove, or an error result.</summary>
    public static GenaResult ParseUnsubscribe(DlnHttpRequest request, out string? sid)
    {
        sid = null;

        // UNSUBSCRIBE carries only SID; CALLBACK or NT here means the client sent the wrong request.
        if (Header(request, "CALLBACK") is { Length: > 0 } || Header(request, "NT") is { Length: > 0 })
        {
            return GenaResult.BadRequest("UNSUBSCRIBE must not carry CALLBACK or NT");
        }

        var raw = Header(request, "SID");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return GenaResult.PreconditionFailed("SID is required");
        }

        if (!IsWellFormedSid(raw))
        {
            return GenaResult.PreconditionFailed("SID is not a valid subscription identifier");
        }

        sid = raw;
        return GenaResult.Ok(new Dictionary<string, string>());
    }

    /// <summary>
    /// Accepts <c>Second-3600</c>, <c>Second-infinite</c> or <c>infinite</c>, clamped to the
    /// configured bounds. An absent header yields the default timeout.
    /// </summary>
    public static bool TryParseTimeout(DlnHttpRequest request, GenaOptions options, out TimeSpan timeout, out string error)
    {
        error = string.Empty;
        timeout = options.DefaultTimeout;

        var raw = Header(request, "TIMEOUT");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var value = raw.Trim();

        if (value.Equals("infinite", StringComparison.OrdinalIgnoreCase))
        {
            timeout = options.MaxTimeout;
            return true;
        }

        const string prefix = "Second-";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "TIMEOUT must be 'Second-N' or 'Second-infinite'";
            return false;
        }

        var number = value[prefix.Length..];
        if (number.Equals("infinite", StringComparison.OrdinalIgnoreCase))
        {
            timeout = options.MaxTimeout;
            return true;
        }

        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
        {
            error = "TIMEOUT must be 'Second-N' or 'Second-infinite'";
            return false;
        }

        // Clamp rather than reject: the spec lets a device grant a different timeout, and clients
        // commonly ask for an hour or for a very short interval.
        timeout = TimeSpan.FromSeconds(seconds);
        if (timeout < options.MinTimeout)
        {
            timeout = options.MinTimeout;
        }
        else if (timeout > options.MaxTimeout)
        {
            timeout = options.MaxTimeout;
        }

        return true;
    }

    /// <summary>GENA SIDs are <c>uuid:</c> followed by a GUID.</summary>
    public static bool IsWellFormedSid(string sid)
        => sid.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(sid[5..], out _);

    /// <summary>Builds the SID value the renderer hands out.</summary>
    public static string CreateSid() => $"uuid:{Guid.NewGuid():D}";

    /// <summary>Formats a granted timeout as <c>Second-N</c>.</summary>
    public static string FormatTimeout(TimeSpan timeout)
        => $"Second-{(int)Math.Round(timeout.TotalSeconds)}";

    private static string? Header(DlnHttpRequest request, string name)
        => request.Headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
