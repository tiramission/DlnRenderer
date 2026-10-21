using System.Net;

namespace DlnRenderer.Dlna;

/// <summary>Configuration for the renderer device exposed on the LAN.</summary>
public sealed record DlnaOptions
{
    public string FriendlyName { get; init; } = "DlnRenderer";

    public string Manufacturer { get; init; } = "DlnRenderer";

    public string ModelName { get; init; } = "DlnRenderer Media Renderer";

    public string ModelNumber { get; init; } = "0.1.0";

    /// <summary>Stable device UUID. Must not change between runs, or control points see a new device.</summary>
    public Guid Uuid { get; init; }

    /// <summary>
    /// HTTP port for description and control endpoints. The service always supplies a concrete
    /// value; 0 (ephemeral) is only used by tests that do not care about a stable address.
    /// </summary>
    public int HttpPort { get; init; }

    /// <summary>Address advertised in <c>LOCATION</c> when the caller cannot infer one.</summary>
    public IPAddress? ListenAddress { get; init; }

    /// <summary>Disable only in tests that exercise HTTP without multicast.</summary>
    public bool EnableSsdp { get; init; } = true;

    /// <summary>How often ssdp:alive is re-announced. Must stay well below the advertised max-age.</summary>
    public TimeSpan AliveInterval { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan MaxAge { get; init; } = TimeSpan.FromMinutes(30);

    public string ServerHeader { get; init; } = "Windows/10 UPnP/1.0 DlnRenderer/0.1";

    /// <summary>GENA eventing limits and callback policy.</summary>
    public Gena.GenaOptions Eventing { get; init; } = new();

    public string Udn => $"uuid:{Uuid:D}";
}
