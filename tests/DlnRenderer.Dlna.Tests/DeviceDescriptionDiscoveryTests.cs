using System.Xml.Linq;
using DlnRenderer.Dlna;
using DlnRenderer.Dlna.Soap;
using DlnRenderer.Dlna.Upnp;

namespace DlnRenderer.Dlna.Tests;

/// <summary>
/// Regression tests for the device description shape that vendor casting clients require.
/// </summary>
/// <remarks>
/// The renderer was answerable over SSDP and its description was reachable, yet casting apps did not
/// list it. The description lacked the DLNA device-profile marker, had no XML declaration and used
/// rooted service URLs, all of which a strict casting client rejects.
/// </remarks>
public class DeviceDescriptionDiscoveryTests
{
    private static readonly Guid Uuid = Guid.Parse("6d2f0c31-9a4b-4e77-b0d5-1f8e2a6c4b93");
    private static readonly XNamespace DeviceNs = "urn:schemas-upnp-org:device-1-0";
    private static readonly XNamespace DlnaNs = "urn:schemas-dlna-org:device-1-0";

    private static DlnaOptions Options => new() { FriendlyName = "Living Room", Uuid = Uuid };

    [Fact]
    public void Description_starts_with_an_xml_declaration()
    {
        var xml = DeviceDescriptionBuilder.Build(Options);

        // A description that does not start with the declaration is rejected by strict parsers.
        Assert.StartsWith("<?xml", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Description_declares_the_dlna_renderer_profile()
    {
        var document = XDocument.Parse(DeviceDescriptionBuilder.Build(Options));

        var marker = document.Descendants(DlnaNs + "X_DLNADOC").SingleOrDefault();

        Assert.NotNull(marker);
        Assert.Equal("DMR-1.50", marker!.Value);
    }

    [Fact]
    public void Description_declares_the_dlna_namespace_on_the_root()
    {
        var document = XDocument.Parse(DeviceDescriptionBuilder.Build(Options));

        var prefix = document.Root!.GetNamespaceOfPrefix("dlna");
        Assert.Equal(DlnaNs, prefix);
    }

    [Theory]
    [InlineData(DlnaPaths.AvTransportScpd)]
    [InlineData(DlnaPaths.AvTransportControl)]
    [InlineData(DlnaPaths.AvTransportEvent)]
    [InlineData(DlnaPaths.RenderingControlScpd)]
    [InlineData(DlnaPaths.RenderingControlControl)]
    [InlineData(DlnaPaths.ConnectionManagerScpd)]
    [InlineData(DlnaPaths.ConnectionManagerControl)]
    public void Service_urls_are_relative_so_they_resolve_against_the_description(string path)
    {
        var document = XDocument.Parse(DeviceDescriptionBuilder.Build(Options));
        var values = document.Descendants()
            .Where(e => e.Name.LocalName is "SCPDURL" or "controlURL" or "eventSubURL")
            .Select(e => e.Value)
            .ToList();

        var expected = path.TrimStart('/');

        Assert.Contains(expected, values);
        Assert.DoesNotContain(values, v => v.StartsWith('/'));
    }

    [Fact]
    public void Relative_service_urls_resolve_to_the_absolute_paths_the_server_serves()
    {
        var document = XDocument.Parse(DeviceDescriptionBuilder.Build(Options));
        var root = document.Root!;

        // A control point resolves a relative URL against the description URL.
        var descriptionUri = new Uri("http://192.168.1.9:18090/description.xml");

        foreach (var element in root.Descendants().Where(e => e.Name.LocalName is "SCPDURL" or "controlURL" or "eventSubURL"))
        {
            var resolved = new Uri(descriptionUri, element.Value);
            Assert.Equal("http", resolved.Scheme);
            Assert.Equal(18090, resolved.Port);
            Assert.StartsWith("/", resolved.AbsolutePath);
        }
    }

    [Fact]
    public void Description_declares_all_three_services_in_a_stable_order()
    {
        var document = XDocument.Parse(DeviceDescriptionBuilder.Build(Options));

        var serviceTypes = document
            .Descendants(DeviceNs + "service")
            .Select(s => s.Element(DeviceNs + "serviceType")!.Value)
            .ToList();

        Assert.Equal(
        [
            SoapNamespaces.AvTransportServiceType,
            SoapNamespaces.RenderingControlServiceType,
            SoapNamespaces.ConnectionManagerServiceType,
        ], serviceTypes);
    }

    [Fact]
    public void Rendering_control_scpd_exposes_volume_and_mute()
    {
        var document = XDocument.Parse(ScpdBuilder.BuildRenderingControl());
        XNamespace serviceNs = "urn:schemas-upnp-org:service-1-0";

        var actions = document.Descendants(serviceNs + "action")
            .Select(a => a.Element(serviceNs + "name")!.Value)
            .ToList();

        Assert.Contains("GetVolume", actions);
        Assert.Contains("SetVolume", actions);
        Assert.Contains("GetMute", actions);
        Assert.Contains("SetMute", actions);
    }
}
