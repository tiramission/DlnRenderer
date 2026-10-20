using DlnRenderer.Dlna.Ssdp;

namespace DlnRenderer.Dlna.Tests;

/// <summary>
/// Regression tests for the LAN discovery failure where control points could not see the renderer.
/// </summary>
/// <remarks>
/// The renderer answered only device-level search targets, so clients that discover renderers by
/// <em>service</em> type (BubbleUPnP searches <c>service:AVTransport:1</c>; vendor casting apps search
/// service types too) never got a reply and the device never appeared in their lists.
/// </remarks>
public class SsdpDiscoveryTests
{
    private static readonly Guid DeviceUuid = Guid.Parse("4f5c1e92-0b7d-4a11-8f3e-2c6d9a7b1e04");

    private static readonly string[] ServiceTargets =
    [
        "urn:schemas-upnp-org:service:AVTransport:1",
        "urn:schemas-upnp-org:service:RenderingControl:1",
        "urn:schemas-upnp-org:service:ConnectionManager:1",
    ];

    [Theory]
    [InlineData("urn:schemas-upnp-org:service:AVTransport:1")]
    [InlineData("urn:schemas-upnp-org:service:RenderingControl:1")]
    [InlineData("urn:schemas-upnp-org:service:ConnectionManager:1")]
    public void Service_type_searches_are_answered(string serviceType)
    {
        // This is the exact failure: BubbleUPnP asks for AVTransport:1 as a service, not a device.
        Assert.True(
            SsdpMessage.Matches(serviceType, DeviceUuid),
            $"a search for {serviceType} must be answered or clients that discover by service type cannot find the renderer");
    }

    [Theory]
    [InlineData("urn:schemas-upnp-org:device:MediaRenderer:1")]
    [InlineData("upnp:rootdevice")]
    [InlineData("ssdp:all")]
    public void Device_level_searches_still_work(string target)
        => Assert.True(SsdpMessage.Matches(target, DeviceUuid));

    [Fact]
    public void Service_type_search_reply_echoes_the_requested_st_and_keeps_the_full_usn()
    {
        var targets = SsdpMessage.TargetsForSearch(ServiceTargets[0], DeviceUuid);

        var (searchTarget, usn) = Assert.Single(targets);

        // SSDP requires the reply's ST to equal the searched ST.
        Assert.Equal(ServiceTargets[0], searchTarget);

        // The USN stays the full "uuid:<udn>::<service>" form.
        Assert.Equal($"uuid:{DeviceUuid:D}::{ServiceTargets[0]}", usn);
    }

    [Fact]
    public void Every_advertised_target_is_answered_with_itself_only()
    {
        var advertised = SsdpMessage.AdvertisedTargets(DeviceUuid);
        Assert.Equal(6, advertised.Count);

        foreach (var (target, usn) in advertised)
        {
            var single = Assert.Single(SsdpMessage.TargetsForSearch(target, DeviceUuid));
            Assert.Equal(target, single.SearchTarget);
            Assert.Equal(usn, single.Usn);
        }
    }

    [Fact]
    public void Ssdp_all_answers_every_advertised_target()
    {
        var targets = SsdpMessage.TargetsForSearch("ssdp:all", DeviceUuid);

        Assert.Equal(SsdpMessage.AdvertisedTargets(DeviceUuid).Count, targets.Count);

        // Order does not matter, but every advertised target must appear exactly once.
        foreach (var (advertisedTarget, advertisedUsn) in SsdpMessage.AdvertisedTargets(DeviceUuid))
        {
            Assert.Contains(targets, t => t.SearchTarget == advertisedTarget && t.Usn == advertisedUsn);
        }
    }

    [Theory]
    [InlineData("urn:schemas-upnp-org:service:ContentDirectory:1")]
    [InlineData("urn:av-openhome-org:service:Product:1")]
    [InlineData("urn:bubblesoftapps-com:service:Main:1")]
    [InlineData("urn:schemas-upnp-org:device:MediaServer:1")]
    [InlineData("urn:schemas-upnp-org:device:InternetGatewayDevice:1")]
    public void Unrelated_targets_are_not_answered(string target)
    {
        // Answering these would claim services we do not implement.
        Assert.False(SsdpMessage.Matches(target, DeviceUuid));
        Assert.Empty(SsdpMessage.TargetsForSearch(target, DeviceUuid));
    }

    [Fact]
    public void Service_search_is_case_insensitive()
    {
        var targets = SsdpMessage.TargetsForSearch(
            "URN:SCHEMAS-UPNP-ORG:SERVICE:AVTRANSPORT:1",
            DeviceUuid);

        // Echoed back with the client's own casing, as required.
        var (searchTarget, _) = Assert.Single(targets);
        Assert.Equal("URN:SCHEMAS-UPNP-ORG:SERVICE:AVTRANSPORT:1", searchTarget);
    }

    [Fact]
    public void Advertised_target_set_covers_device_and_all_three_services()
    {
        var targets = SsdpMessage.AdvertisedTargets(DeviceUuid).Select(t => t.SearchTarget).ToList();

        Assert.Contains(SsdpConstants.RootDevice, targets);
        Assert.Contains($"uuid:{DeviceUuid:D}", targets);
        Assert.Contains(SsdpConstants.MediaRendererDeviceType, targets);

        foreach (var serviceType in ServiceTargets)
        {
            Assert.Contains(serviceType, targets);
        }
    }
}
