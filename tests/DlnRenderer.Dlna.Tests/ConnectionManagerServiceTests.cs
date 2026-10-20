using System.Xml.Linq;
using DlnRenderer.Dlna.AvTransport;
using DlnRenderer.Dlna.Soap;
using DlnRenderer.Dlna.Tests.Support;

namespace DlnRenderer.Dlna.Tests;

public class ConnectionManagerServiceTests
{
    private static Dictionary<string, string> Values(string xml, string action)
    {
        var document = XDocument.Parse(xml);
        var body = document.Root!.Elements().Single(e => e.Name.LocalName == "Body");
        var response = body.Elements().Single();
        Assert.Equal(action + "Response", response.Name.LocalName);
        Assert.Equal(SoapNamespaces.ConnectionManagerServiceType, response.Name.Namespace);
        return response.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
    }

    [Fact]
    public void GetProtocolInfo_declares_http_get_sink_only()
    {
        var service = new ConnectionManagerService();
        var values = Values(
            service.Handle(SoapBody.Parse(SoapNamespaces.ConnectionManagerServiceType, "GetProtocolInfo")),
            "GetProtocolInfo");

        Assert.Equal(string.Empty, values["Source"]);

        // The sink list must enumerate real formats; a wildcard makes control points believe the
        // renderer accepts nothing. See ConnectionManagerProtocolInfoTests.
        Assert.NotEqual("http-get:*:*:*", values["Sink"]);
        Assert.Contains("http-get:*:video/mp4", values["Sink"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("http-get:*:audio/mpeg", values["Sink"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCurrentConnectionIDs_reports_the_implicit_connection()
    {
        var service = new ConnectionManagerService();
        var values = Values(
            service.Handle(SoapBody.Parse(SoapNamespaces.ConnectionManagerServiceType, "GetCurrentConnectionIDs")),
            "GetCurrentConnectionIDs");

        Assert.Equal("0", values["ConnectionIDs"]);
    }

    [Fact]
    public void GetCurrentConnectionInfo_describes_an_input_connection()
    {
        var service = new ConnectionManagerService();
        var values = Values(
            service.Handle(SoapBody.Parse(
                SoapNamespaces.ConnectionManagerServiceType,
                "GetCurrentConnectionInfo",
                ("ConnectionID", "0"))),
            "GetCurrentConnectionInfo");

        Assert.Equal("Input", values["Direction"]);
        Assert.Equal("OK", values["Status"]);
        Assert.Equal("0", values["AVTransportID"]);
        Assert.Equal("-1", values["RcsID"]);
    }

    [Fact]
    public void Unknown_connection_reference_faults()
    {
        var service = new ConnectionManagerService();
        var error = Assert.Throws<UpnpException>(() => service.Handle(
            SoapBody.Parse(
                SoapNamespaces.ConnectionManagerServiceType,
                "GetCurrentConnectionInfo",
                ("ConnectionID", "7"))));

        Assert.Equal(706, error.Code);
    }

    [Fact]
    public void Unknown_action_faults_with_401()
    {
        var service = new ConnectionManagerService();
        var error = Assert.Throws<UpnpException>(() => service.Handle(
            SoapBody.Parse(SoapNamespaces.ConnectionManagerServiceType, "PrepareForConnection")));

        Assert.Equal(UpnpErrorCode.InvalidAction, error.Code);
    }
}
