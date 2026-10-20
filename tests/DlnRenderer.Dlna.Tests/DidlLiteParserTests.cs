using DlnRenderer.Dlna.Metadata;

namespace DlnRenderer.Dlna.Tests;

public class DidlLiteParserTests
{
    [Fact]
    public void Extracts_common_item_fields()
    {
        const string didl = """
            <DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"
                       xmlns:dc="http://purl.org/dc/elements/1.1/"
                       xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/">
              <item id="1" parentID="0" restricted="1">
                <dc:title>Big Buck Bunny</dc:title>
                <dc:creator>Blender</dc:creator>
                <upnp:album>Demo</upnp:album>
                <upnp:class>object.item.videoItem</upnp:class>
                <upnp:albumArtURI>http://192.168.1.5/cover.jpg</upnp:albumArtURI>
                <res protocolInfo="http-get:*:video/mp4:*" duration="0:09:56.000">http://192.168.1.5/video.mp4</res>
              </item>
            </DIDL-Lite>
            """;

        var metadata = DidlLiteParser.Parse(didl);

        Assert.Equal("Big Buck Bunny", metadata.Title);
        Assert.Equal("Blender", metadata.Artist);
        Assert.Equal("Demo", metadata.Album);
        Assert.Equal("object.item.videoItem", metadata.UpnpClass);
        Assert.Equal("http://192.168.1.5/cover.jpg", metadata.AlbumArtUri);
        Assert.Equal("video/mp4", metadata.MimeType);
        Assert.Equal(TimeSpan.FromSeconds(596), metadata.Duration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<DIDL-Lite><item><title>unclosed</DIDL-Lite>")]
    [InlineData("not xml at all")]
    [InlineData("<DIDL-Lite></DIDL-Lite>")]
    public void Returns_empty_metadata_for_missing_or_broken_input(string? didl)
    {
        var metadata = DidlLiteParser.Parse(didl);
        Assert.Null(metadata.Title);
        Assert.Null(metadata.Duration);
    }

    [Fact]
    public void Handles_missing_namespaces_and_unknown_elements()
    {
        const string didl = "<item><title>No namespace</title><unknown>value</unknown></item>";
        Assert.Equal("No namespace", DidlLiteParser.Parse(didl).Title);
    }

    [Fact]
    public void Does_not_resolve_external_entities()
    {
        const string didl = """
            <!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]>
            <DIDL-Lite><item><title>&xxe;</title></item></DIDL-Lite>
            """;

        var metadata = DidlLiteParser.Parse(didl);
        Assert.Null(metadata.Title);
    }

    [Fact]
    public void Ignores_a_wildcard_mime_type()
    {
        const string didl = """
            <DIDL-Lite><item><title>t</title>
              <res protocolInfo="http-get:*:*:*">http://host/a.bin</res>
            </item></DIDL-Lite>
            """;

        Assert.Null(DidlLiteParser.Parse(didl).MimeType);
    }
}
