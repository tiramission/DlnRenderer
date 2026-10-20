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

    /// <summary>HTTP port for description and control endpoints. 0 lets the OS pick one.</summary>
    public int HttpPort { get; init; }

    /// <summary>Address advertised in LOCATION. Defaults to the first suitable LAN address.</summary>
    public IPAddress? ListenAddress { get; init; }

    /// <summary>Disable only in tests that exercise HTTP without multicast.</summary>
    public bool EnableSsdp { get; init; } = true;

    /// <summary>How often ssdp:alive is re-announced. Must stay well below the advertised max-age.</summary>
    public TimeSpan AliveInterval { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan MaxAge { get; init; } = TimeSpan.FromMinutes(30);

    public string ServerHeader { get; init; } = "Windows/10.0 UPnP/1.1 DlnRenderer/0.1";

    public string Udn => $"uuid:{Uuid:D}";
}
