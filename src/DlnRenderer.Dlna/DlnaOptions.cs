using System.Net;

namespace DlnRenderer.Dlna;

/// <summary>Configuration for the renderer device exposed on the LAN.</summary>
public sealed record DlnaOptions
{
    public string FriendlyName { get; init; } = "DlnRenderer";

    public string Manufacturer { get; init; } = "DlnRenderer";

    /// <summary>Advertised in the device description; casting clients show it in the device entry.</summary>
    public string ManufacturerUrl { get; init; } = "https://github.com/tiramission/DlnRenderer";

    public string ModelName { get; init; } = "DlnRenderer Media Renderer";

    public string ModelNumber { get; init; } = "0.1.0";

    public string ModelUrl { get; init; } = "https://github.com/tiramission/DlnRenderer";

    /// <summary>A stable, non-empty value; some clients index into a fixed field list and drop a
    /// device whose serialNumber is missing entirely.</summary>
    public string SerialNumber { get; init; } = "1";

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

    /// <summary>
    /// How often ssdp:alive is re-announced. Must stay well below the advertised max-age.
    /// </summary>
    /// <remarks>
    /// 30 s / 66 s matches the reference renderer Chinese casting apps are verified against. The
    /// short max-age makes a control point forget a renderer whose announcements stopped (crash,
    /// sleep, network change) within about a minute instead of keeping a dead entry for half an hour.
    /// </remarks>
    public TimeSpan AliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxAge { get; init; } = TimeSpan.FromSeconds(66);

    public string ServerHeader { get; init; } = "Windows/10 UPnP/1.0 DlnRenderer/0.1";

    /// <summary>GENA eventing limits and callback policy.</summary>
    public Gena.GenaOptions Eventing { get; init; } = new();

    public string Udn => $"uuid:{Uuid:D}";
}
