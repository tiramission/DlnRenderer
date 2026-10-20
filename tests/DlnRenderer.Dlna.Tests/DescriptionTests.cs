using System.Xml.Linq;
using DlnRenderer.Dlna;
using DlnRenderer.Dlna.Soap;
using DlnRenderer.Dlna.Upnp;

namespace DlnRenderer.Dlna.Tests;

public class DescriptionTests
{
    private static readonly Guid Uuid = Guid.Parse("ef7c8b80-2f6f-4a3b-9c1e-0a2d4f6b8c11");

    private static readonly XNamespace Device = "urn:schemas-upnp-org:device-1-0";
    private static readonly XNamespace Service = "urn:schemas-upnp-org:service-1-0";

    private static DlnaOptions Options => new()
    {
        FriendlyName = "Living Room Renderer",
        Uuid = Uuid,
    };

    [Fact]
    public void Device_description_declares_renderer_identity_and_services()
    {
        var document = XDocument.Parse(DeviceDescriptionBuilder.Build(Options));
        var root = document.Root!;

        Assert.Equal(Device + "root", root.Name);
        var device = root.Element(Device + "device")!;
        Assert.Equal("urn:schemas-upnp-org:device:MediaRenderer:1", device.Element(Device + "deviceType")!.Value);
        Assert.Equal("Living Room Renderer", device.Element(Device + "friendlyName")!.Value);
        Assert.Equal($"uuid:{Uuid:D}", device.Element(Device + "UDN")!.Value);
        Assert.False(string.IsNullOrWhiteSpace(device.Element(Device + "manufacturer")!.Value));
        Assert.False(string.IsNullOrWhiteSpace(device.Element(Device + "modelName")!.Value));
        Assert.Equal("1", root.Element(Device + "specVersion")!.Element(Device + "major")!.Value);
    }

    [Fact]
    public void Device_description_lists_exactly_the_implemented_services()
    {
        var document = XDocument.Parse(DeviceDescriptionBuilder.Build(Options));
        var services = document.Descendants(Device + "service").ToList();

        Assert.Equal(2, services.Count);
        var types = services.Select(s => s.Element(Device + "serviceType")!.Value).ToList();
        Assert.Contains(SoapNamespaces.AvTransportServiceType, types);
        Assert.Contains(SoapNamespaces.ConnectionManagerServiceType, types);

        foreach (var service in services)
        {
            var control = service.Element(Device + "controlURL")!.Value;
            var scpd = service.Element(Device + "SCPDURL")!.Value;
            var events = service.Element(Device + "eventSubURL")!.Value;
            Assert.StartsWith("/", control, StringComparison.Ordinal);
            Assert.StartsWith("/scpd/", scpd, StringComparison.Ordinal);
            Assert.StartsWith("/event/", events, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AvTransport_scpd_declares_the_supported_actions()
    {
        var document = XDocument.Parse(ScpdBuilder.BuildAvTransport());
        var actions = document.Descendants(Service + "action")
            .Select(a => a.Element(Service + "name")!.Value)
            .ToList();

        foreach (var required in new[]
                 {
                     "SetAVTransportURI", "Play", "Pause", "Stop", "Seek",
                     "GetTransportInfo", "GetPositionInfo", "GetMediaInfo",
                     "GetDeviceCapabilities", "GetTransportSettings",
                 })
        {
            Assert.Contains(required, actions);
        }

        Assert.DoesNotContain("Next", actions);
        Assert.DoesNotContain("Previous", actions);
        Assert.DoesNotContain("SetNextAVTransportURI", actions);

        var play = document.Descendants(Service + "action")
            .Single(a => a.Element(Service + "name")!.Value == "Play");
        var arguments = play.Element(Service + "argumentList")!.Elements(Service + "argument").ToList();
        Assert.Equal(2, arguments.Count);
        Assert.Contains(arguments, a => a.Element(Service + "name")!.Value == "Speed"
            && a.Element(Service + "direction")!.Value == "in");
    }

    [Fact]
    public void AvTransport_state_variables_match_declared_values()
    {
        var document = XDocument.Parse(ScpdBuilder.BuildAvTransport());
        var variables = document.Descendants(Service + "stateVariable")
            .ToDictionary(v => v.Element(Service + "name")!.Value);

        var transportState = variables["TransportState"];
        Assert.Equal("no", transportState.Attribute("sendEvents")!.Value);
        var allowed = transportState.Descendants(Service + "allowedValue").Select(v => v.Value).ToList();
        Assert.Contains("PLAYING", allowed);
        Assert.Contains("PAUSED_PLAYBACK", allowed);
        Assert.Contains("TRANSITIONING", allowed);
        Assert.Contains("STOPPED", allowed);

        // Every action argument must reference a declared state variable.
        var declared = variables.Keys.ToHashSet(StringComparer.Ordinal);
        var referenced = document.Descendants(Service + "relatedStateVariable").Select(e => e.Value);
        Assert.All(referenced, name => Assert.Contains(name, declared));
    }

    [Fact]
    public void ConnectionManager_scpd_declares_protocol_negotiation_only()
    {
        var document = XDocument.Parse(ScpdBuilder.BuildConnectionManager());
        var actions = document.Descendants(Service + "action")
            .Select(a => a.Element(Service + "name")!.Value)
            .ToList();

        Assert.Equal(["GetProtocolInfo", "GetCurrentConnectionIDs", "GetCurrentConnectionInfo"], actions);
    }
}
