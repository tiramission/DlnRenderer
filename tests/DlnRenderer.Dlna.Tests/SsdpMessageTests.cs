using DlnRenderer.Dlna.Ssdp;

namespace DlnRenderer.Dlna.Tests;

public class SsdpMessageTests
{
    private static readonly Guid DeviceUuid = Guid.Parse("6f1a9c1a-4d33-4a41-9a6d-3f6d9c2b7a10");

    private static string Search(string st, int mx = 1)
        => "M-SEARCH * HTTP/1.1\r\n"
           + "HOST: 239.255.255.250:1900\r\n"
           + "MAN: \"ssdp:discover\"\r\n"
           + $"MX: {mx}\r\n"
           + $"ST: {st}\r\n"
           + "\r\n";

    [Theory]
    [InlineData("ssdp:all")]
    [InlineData("upnp:rootdevice")]
    [InlineData("urn:schemas-upnp-org:device:MediaRenderer:1")]
    public void Parses_standard_searches(string searchTarget)
    {
        Assert.True(SsdpMessage.TryParseSearch(Search(searchTarget, 3), out var request));
        Assert.NotNull(request);
        Assert.Equal(searchTarget, request.SearchTarget);
        Assert.Equal(3, request.Mx);
    }

    [Fact]
    public void Parses_the_device_uuid_target()
    {
        var udn = $"uuid:{DeviceUuid:D}";
        Assert.True(SsdpMessage.TryParseSearch(Search(udn), out var request));
        Assert.Equal(udn, request!.SearchTarget);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NOTIFY * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\n\r\n")]
    [InlineData("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nST: ssdp:all\r\n\r\n")]
    [InlineData("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\n\r\n")]
    public void Rejects_malformed_requests(string payload)
        => Assert.False(SsdpMessage.TryParseSearch(payload, out _));

    [Theory]
    [InlineData("0", 1)]
    [InlineData("1", 1)]
    [InlineData("9", 5)]
    [InlineData("not-a-number", 1)]
    [InlineData("-4", 1)]
    public void Clamps_mx_into_a_bounded_window(string mx, int expected)
    {
        var payload = Search("ssdp:all").Replace("MX: 1", $"MX: {mx}");
        Assert.True(SsdpMessage.TryParseSearch(payload, out var request));
        Assert.Equal(expected, request!.Mx);
    }

    [Theory]
    [InlineData("ssdp:all", true)]
    [InlineData("upnp:rootdevice", true)]
    [InlineData("urn:schemas-upnp-org:device:MediaRenderer:1", true)]
    [InlineData("uuid:11111111-2222-3333-4444-555555555555", false)]
    [InlineData("urn:schemas-upnp-org:service:AVTransport:1", true)]
    [InlineData("urn:schemas-upnp-org:device:MediaServer:1", false)]
    [InlineData("urn:schemas-upnp-org:service:ContentDirectory:1", false)]
    public void Matches_only_targets_this_device_answers(string searchTarget, bool expected)
    {
        Assert.Equal(expected, SsdpMessage.Matches(searchTarget, DeviceUuid));
    }

    [Fact]
    public void Advertises_root_uuid_and_device_type_with_stable_usn()
    {
        var targets = SsdpMessage.AdvertisedTargets(DeviceUuid);

        // Root, the bare udn, the device type and one entry per implemented service.
        Assert.Equal(6, targets.Count);
        Assert.Contains(targets, t => t.SearchTarget == "upnp:rootdevice" && t.Usn == $"uuid:{DeviceUuid:D}::upnp:rootdevice");
        Assert.Contains(targets, t => t.SearchTarget == $"uuid:{DeviceUuid:D}" && t.Usn == $"uuid:{DeviceUuid:D}");
        Assert.Contains(targets, t => t.SearchTarget == SsdpConstants.MediaRendererDeviceType);
    }

    [Fact]
    public void Search_response_carries_the_required_headers()
    {
        var response = SsdpMessage.BuildSearchResponse(
            SsdpConstants.MediaRendererDeviceType,
            $"uuid:{DeviceUuid:D}::{SsdpConstants.MediaRendererDeviceType}",
            "http://192.168.1.20:49152/description.xml",
            "Windows/10.0 UPnP/1.1 DlnRenderer/0.1",
            TimeSpan.FromMinutes(30));

        Assert.StartsWith("HTTP/1.1 200 OK\r\n", response, StringComparison.Ordinal);
        Assert.Contains("CACHE-CONTROL: max-age=1800\r\n", response, StringComparison.Ordinal);
        Assert.Contains("LOCATION: http://192.168.1.20:49152/description.xml\r\n", response, StringComparison.Ordinal);
        Assert.Contains("SERVER: Windows/10.0 UPnP/1.1 DlnRenderer/0.1\r\n", response, StringComparison.Ordinal);
        Assert.Contains($"ST: {SsdpConstants.MediaRendererDeviceType}\r\n", response, StringComparison.Ordinal);
        Assert.Contains($"USN: uuid:{DeviceUuid:D}", response, StringComparison.Ordinal);
        Assert.Contains("EXT:\r\n", response, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n", response, StringComparison.Ordinal);
    }

    [Fact]
    public void Alive_notification_defaults_to_byebye_without_location()
    {
        var alive = SsdpMessage.BuildNotify(
            "upnp:rootdevice",
            alive: true,
            $"uuid:{DeviceUuid:D}::upnp:rootdevice",
            "http://192.168.1.20:49152/description.xml",
            "Windows/10.0 UPnP/1.1 DlnRenderer/0.1",
            TimeSpan.FromMinutes(30));

        var byebye = SsdpMessage.BuildNotify(
            "upnp:rootdevice",
            alive: false,
            $"uuid:{DeviceUuid:D}::upnp:rootdevice",
            "http://192.168.1.20:49152/description.xml",
            "Windows/10.0 UPnP/1.1 DlnRenderer/0.1",
            TimeSpan.FromMinutes(30));

        Assert.StartsWith("NOTIFY * HTTP/1.1\r\n", alive, StringComparison.Ordinal);
        Assert.Contains("NTS: ssdp:alive\r\n", alive, StringComparison.Ordinal);
        Assert.Contains("LOCATION: http://192.168.1.20:49152/description.xml\r\n", alive, StringComparison.Ordinal);
        Assert.Contains("NTS: ssdp:byebye\r\n", byebye, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCATION:", byebye, StringComparison.Ordinal);
        Assert.DoesNotContain("CACHE-CONTROL:", byebye, StringComparison.Ordinal);
    }

    [Fact]
    public void Uuid_is_supplied_by_the_caller_and_never_regenerated()
    {
        var first = SsdpMessage.AdvertisedTargets(DeviceUuid)[1];
        var second = SsdpMessage.AdvertisedTargets(DeviceUuid)[1];
        Assert.Equal(first, second);
    }
}
