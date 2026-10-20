using System.Xml.Linq;
using DlnRenderer.Dlna.AvTransport;
using DlnRenderer.Dlna.Soap;

namespace DlnRenderer.Dlna.Tests;

/// <summary>
/// Regression tests for the "discovered but not playable" failure.
/// </summary>
/// <remarks>
/// The renderer advertised <c>http-get:*:*:*</c> - a wildcard - as its only sink format. Control
/// points negotiate against this list before pushing media, and many of them treat a wildcard-only
/// list as "this renderer accepts nothing", so the device stayed out of their casting menus while
/// remaining visible over SSDP.
/// </remarks>
public class ConnectionManagerProtocolInfoTests
{
    private static string GetSink()
    {
        var service = new ConnectionManagerService();
        var request = new SoapRequest(XElement.Parse(
            """
            <u:GetProtocolInfo xmlns:u="urn:schemas-upnp-org:service:ConnectionManager:1" />
            """));

        var response = service.Handle(request);
        var document = XDocument.Parse(response);

        var sink = document.Descendants().Single(e => e.Name.LocalName == "Sink").Value;
        return sink;
    }

    [Fact]
    public void Sink_list_is_an_explicit_enumeration_not_a_wildcard()
    {
        var sink = GetSink();

        Assert.False(
            string.IsNullOrWhiteSpace(sink),
            "an empty sink list tells control points the renderer accepts no media");

        Assert.NotEqual("http-get:*:*:*", sink);
        Assert.True((sink.Split(',').Length) > 100, "the sink list must enumerate concrete formats");
    }

    [Theory]
    [InlineData("video/mp4")]
    [InlineData("video/x-matroska")]
    [InlineData("audio/mpeg")]
    [InlineData("audio/flac")]
    [InlineData("video/x-msvideo")]
    [InlineData("video/vnd.dlna.mpeg-tts")]
    public void Sink_list_covers_the_formats_control_points_push(string mimeType)
        => Assert.Contains(mimeType, GetSink(), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Sink_list_includes_dlna_profiles()
    {
        // DLNA-aware clients match on DLNA.ORG_PN profiles, not only on MIME types.
        Assert.Contains("DLNA.ORG_PN", GetSink(), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_sink_entry_is_http_get()
    {
        var sink = GetSink();

        foreach (var entry in sink.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.StartsWith("http-get:", entry, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Renderer_does_not_claim_to_source_content()
    {
        var service = new ConnectionManagerService();
        var request = new SoapRequest(XElement.Parse(
            """
            <u:GetProtocolInfo xmlns:u="urn:schemas-upnp-org:service:ConnectionManager:1" />
            """));

        var document = XDocument.Parse(service.Handle(request));

        var source = document.Descendants().Single(e => e.Name.LocalName == "Source").Value;
        Assert.Equal(string.Empty, source);
    }
}
