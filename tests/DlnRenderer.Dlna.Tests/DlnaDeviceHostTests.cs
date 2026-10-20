using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using DlnRenderer.Core.Models;
using DlnRenderer.Dlna;
using DlnRenderer.Dlna.Soap;
using DlnRenderer.Dlna.Tests.Support;

namespace DlnRenderer.Dlna.Tests;

public class DlnaDeviceHostTests
{
    private static readonly Guid Uuid = Guid.Parse("ac1e2f34-56b7-4c8d-9e0f-112233445566");

    private static DlnaOptions LoopbackOptions(int port = 0) => new()
    {
        FriendlyName = "Test Renderer",
        Uuid = Uuid,
        HttpPort = port,
        ListenAddress = IPAddress.Loopback,
        EnableSsdp = false,
    };

    private static StringContent SoapContent(string serviceType, string action, params (string Name, string Value)[] arguments)
    {
        var content = new StringContent(
            SoapBody.Build(serviceType, action, arguments),
            Encoding.UTF8,
            "text/xml");
        content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
        content.Headers.TryAddWithoutValidation("SOAPAction", $"\"{serviceType}#{action}\"");
        return content;
    }

    [Fact]
    public async Task Description_and_scpd_are_served_over_http()
    {
        await using var host = new DlnaDeviceHost(LoopbackOptions(), new FakeTransportRenderer());
        host.Start();

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.HttpPort}") };

        Assert.NotNull(host.DescriptionUrl);
        Assert.StartsWith($"http://127.0.0.1:{host.HttpPort}/", host.DescriptionUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", host.DescriptionUrl, StringComparison.OrdinalIgnoreCase);

        var description = XDocument.Parse(await client.GetStringAsync("/description.xml"));
        Assert.Equal("urn:schemas-upnp-org:device:MediaRenderer:1",
            description.Descendants().Single(e => e.Name.LocalName == "deviceType").Value);

        var avTransportScpd = XDocument.Parse(await client.GetStringAsync("/scpd/avtransport.xml"));
        Assert.Contains(
            avTransportScpd.Descendants(),
            e => e.Name.LocalName == "name" && e.Value == "SetAVTransportURI");

        var connectionManagerScpd = await client.GetStringAsync("/scpd/connectionmanager.xml");
        Assert.Contains("GetProtocolInfo", connectionManagerScpd, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Soap_control_drives_the_renderer()
    {
        var renderer = new FakeTransportRenderer();
        await using var host = new DlnaDeviceHost(LoopbackOptions(), renderer);
        host.Start();

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.HttpPort}") };

        using var setUri = await client.PostAsync(
            "/control/avtransport",
            SoapContent(
                SoapNamespaces.AvTransportServiceType,
                "SetAVTransportURI",
                ("InstanceID", "0"),
                ("CurrentURI", "http://127.0.0.1:9999/video.mp4"),
                ("CurrentURIMetaData", "<DIDL-Lite><item><title>Movie</title></item></DIDL-Lite>")));

        Assert.Equal(HttpStatusCode.OK, setUri.StatusCode);
        Assert.Equal(new Uri("http://127.0.0.1:9999/video.mp4"), renderer.CurrentUri);

        using var play = await client.PostAsync(
            "/control/avtransport",
            SoapContent(SoapNamespaces.AvTransportServiceType, "Play", ("InstanceID", "0"), ("Speed", "1")));

        Assert.Equal(HttpStatusCode.OK, play.StatusCode);
        Assert.Equal(PlaybackState.Playing, renderer.State);

        var infoBody = await (await client.PostAsync(
            "/control/avtransport",
            SoapContent(SoapNamespaces.AvTransportServiceType, "GetTransportInfo", ("InstanceID", "0"))))
            .Content.ReadAsStringAsync();

        Assert.Contains("<CurrentTransportState>PLAYING</CurrentTransportState>", infoBody, StringComparison.Ordinal);

        var cmBody = await (await client.PostAsync(
            "/control/connectionmanager",
            SoapContent(SoapNamespaces.ConnectionManagerServiceType, "GetProtocolInfo")))
            .Content.ReadAsStringAsync();

        Assert.Contains("http-get:*:*:*", cmBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Soap_faults_are_returned_with_a_500_and_upnp_error_code()
    {
        await using var host = new DlnaDeviceHost(LoopbackOptions(), new FakeTransportRenderer());
        host.Start();

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.HttpPort}") };

        using var response = await client.PostAsync(
            "/control/avtransport",
            SoapContent(SoapNamespaces.AvTransportServiceType, "GetTransportInfo", ("InstanceID", "9")));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("<errorCode>718</errorCode>", body, StringComparison.Ordinal);
        Assert.Contains("UPnPError", body, StringComparison.Ordinal);

        // The XML prolog must agree with the encoded body: a "utf-16" declaration on utf-8 bytes
        // makes strict control-point parsers reject the fault.
        Assert.Contains("encoding=\"utf-8\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("utf-16", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_methods_and_unknown_paths_are_rejected()
    {
        await using var host = new DlnaDeviceHost(LoopbackOptions(), new FakeTransportRenderer());
        host.Start();

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.HttpPort}") };

        var getControl = await client.GetAsync("/control/avtransport");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getControl.StatusCode);

        var subscribe = new HttpRequestMessage(HttpMethod.Parse("SUBSCRIBE"), "/event/avtransport");
        var subscribeResponse = await client.SendAsync(subscribe);
        Assert.Equal(HttpStatusCode.NotImplemented, subscribeResponse.StatusCode);

        var missing = await client.GetAsync("/nope");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var malformed = await client.PostAsync("/control/avtransport", new StringContent("<oops/>", Encoding.UTF8, "text/xml"));
        Assert.Equal(HttpStatusCode.InternalServerError, malformed.StatusCode);
        Assert.Contains("<errorCode>402</errorCode>", await malformed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_can_be_restarted_on_the_same_port_after_dispose()
    {
        var probe = new DlnaDeviceHost(LoopbackOptions(), new FakeTransportRenderer());
        probe.Start();
        var port = probe.HttpPort;
        await probe.DisposeAsync();

        await using var rebound = new DlnaDeviceHost(LoopbackOptions(port), new FakeTransportRenderer());
        rebound.Start();
        Assert.Equal(port, rebound.HttpPort);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        Assert.True((await client.GetAsync("/description.xml")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Starting_the_same_host_twice_is_rejected()
    {
        await using var host = new DlnaDeviceHost(LoopbackOptions(), new FakeTransportRenderer());
        host.Start();

        Assert.Throws<InvalidOperationException>(() => host.Start());
    }

    [Fact]
    public async Task Port_conflict_is_reported_instead_of_silently_ignored()
    {
        await using var first = new DlnaDeviceHost(LoopbackOptions(), new FakeTransportRenderer());
        first.Start();

        await using var second = new DlnaDeviceHost(LoopbackOptions(first.HttpPort), new FakeTransportRenderer());
        Assert.ThrowsAny<Exception>(() => second.Start());
    }
}
