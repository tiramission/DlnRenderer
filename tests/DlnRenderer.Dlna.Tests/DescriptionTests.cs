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

        // Casting clients build their device entry from a fixed field list; a missing element is not
        // an XML error but can drop the device from the list entirely.
        foreach (var required in new[] { "manufacturerURL", "modelURL", "serialNumber", "modelNumber", "modelDescription" })
        {
            Assert.False(
                string.IsNullOrWhiteSpace(device.Element(Device + required)?.Value),
                $"device description is missing {required}");
        }

        Assert.Equal("DMR-1.50", device.Element(UpnpNamespaces.Dlna + "X_DLNADOC")!.Value);
    }

    [Fact]
    public void Device_description_lists_exactly_the_implemented_services()
    {
        var document = XDocument.Parse(DeviceDescriptionBuilder.Build(Options));
        var services = document.Descendants(Device + "service").ToList();

        // Three services: AVTransport, RenderingControl (needed by casting clients) and ConnectionManager.
        Assert.Equal(3, services.Count);
        var types = services.Select(s => s.Element(Device + "serviceType")!.Value).ToList();
        Assert.Contains(SoapNamespaces.AvTransportServiceType, types);
        Assert.Contains(SoapNamespaces.RenderingControlServiceType, types);
        Assert.Contains(SoapNamespaces.ConnectionManagerServiceType, types);

        foreach (var service in services)
        {
            var control = service.Element(Device + "controlURL")!.Value;
            var scpd = service.Element(Device + "SCPDURL")!.Value;
            var events = service.Element(Device + "eventSubURL")!.Value;
            // Relative URLs (no leading slash) resolve under the description URL's path, which is
            // what casting clients expect and what keeps them working on multi-homed hosts.
            Assert.False(control.StartsWith('/'), $"controlURL should be relative, was '{control}'");
            Assert.False(scpd.StartsWith('/'), $"SCPDURL should be relative, was '{scpd}'");
            Assert.False(events.StartsWith('/'), $"eventSubURL should be relative, was '{events}'");
            Assert.StartsWith("scpd/", scpd, StringComparison.Ordinal);
            Assert.StartsWith("event/", events, StringComparison.Ordinal);
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
                     "GetCurrentTransportActions", "SetPlayMode",
                 })
        {
            Assert.Contains(required, actions);
        }

        // Queue navigation is declared (and answered as a no-op): casting clients read the SCPD to
        // decide which controls their remote UI may show, and an absent action is treated as
        // "this device cannot do it" even though a SOAP call would have succeeded.
        Assert.Contains("Next", actions);
        Assert.Contains("Previous", actions);

        // A queue the renderer cannot honour must not be advertised at all.
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
        Assert.Contains("NO_MEDIA_PRESENT", allowed);

        // Every action argument must reference a declared state variable.
        var declared = variables.Keys.ToHashSet(StringComparer.Ordinal);
        var referenced = document.Descendants(Service + "relatedStateVariable").Select(e => e.Value);
        Assert.All(referenced, name => Assert.Contains(name, declared));
    }

    [Fact]
    public void Scpds_are_serialized_without_namespace_prefixes()
    {
        // Clients that walk the document by tag name - rather than by namespace URI - find no
        // actions at all when the root is written as <s:scpd>. Discovery then succeeds while the
        // device silently drops out of every cast menu.
        foreach (var scpd in new[]
                 {
                     ScpdBuilder.BuildAvTransport(),
                     ScpdBuilder.BuildConnectionManager(),
                     ScpdBuilder.BuildRenderingControl(),
                 })
        {
            var document = XDocument.Parse(scpd);
            Assert.Equal(Service, document.Root!.Name.Namespace);

            // The namespace must be declared as the default one and nothing else: a prefix on the
            // root element is what makes tag-name lookups fail.
            Assert.Contains("<scpd xmlns=\"urn:schemas-upnp-org:service-1-0\">", scpd, StringComparison.Ordinal);
            Assert.DoesNotContain("<s:", scpd, StringComparison.Ordinal);
        }
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
