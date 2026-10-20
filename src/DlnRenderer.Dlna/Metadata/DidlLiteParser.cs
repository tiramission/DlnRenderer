using System.Xml;
using System.Xml.Linq;
using DlnRenderer.Dlna.Upnp;

namespace DlnRenderer.Dlna.Metadata;

/// <summary>Metadata extracted from a control point's DIDL-Lite document.</summary>
public sealed record DidlMetadata
{
    public static DidlMetadata Empty { get; } = new();

    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? Album { get; init; }

    public string? UpnpClass { get; init; }

    public string? AlbumArtUri { get; init; }

    public TimeSpan? Duration { get; init; }

    public string? MimeType { get; init; }
}

/// <summary>
/// Parses DIDL-Lite defensively: control points send truncated, mis-encoded and namespace-less
/// XML, and metadata must never fail the transport request.
/// </summary>
public static class DidlLiteParser
{
    public static DidlMetadata Parse(string? didl)
    {
        if (string.IsNullOrWhiteSpace(didl))
        {
            return DidlMetadata.Empty;
        }

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                MaxCharactersInDocument = 512 * 1024,
            };

            using var stringReader = new StringReader(didl);
            using var reader = XmlReader.Create(stringReader, settings);
            var document = XDocument.Load(reader);

            // Match by local name: the DIDL namespace is frequently wrong or missing.
            var item = document.Descendants().FirstOrDefault(e =>
                e.Name.LocalName is "item" or "videoItem" or "audioItem" or "imageItem");
            if (item is null)
            {
                return DidlMetadata.Empty;
            }

            string? Value(string name) =>
                item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim();

            // DIDL-Lite uses dc:creator for the artist; some control points send upnp:artist.
            string? artist = Value("artist") ?? Value("creator");

            var res = item.Descendants().FirstOrDefault(e => e.Name.LocalName == "res");

            // DIDL durations use the UPnP time format (H+:MM:SS[.f]), not TimeSpan's own syntax.
            // The value is normally an attribute of <res>, but some control points send an element.
            var durationText = res?.Attribute("duration")?.Value ?? Value("duration");
            TimeSpan? duration = UpnpTime.TryParse(durationText, out var parsed)
                ? parsed
                : null;

            var albumArt = item.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "albumArtURI")?.Value?.Trim();

            var mimeSource = res?.Attribute("protocolInfo")?.Value;

            return new DidlMetadata
            {
                Title = Value("title"),
                Artist = artist,
                Album = Value("album"),
                UpnpClass = Value("class"),
                AlbumArtUri = albumArt,
                Duration = duration,
                MimeType = ParseMimeType(mimeSource),
            };
        }
        catch (XmlException)
        {
            return DidlMetadata.Empty;
        }
        catch (InvalidOperationException)
        {
            return DidlMetadata.Empty;
        }
    }

    /// <summary>protocolInfo looks like "http-get:*:video/mp4:*".</summary>
    private static string? ParseMimeType(string? protocolInfo)
    {
        if (string.IsNullOrWhiteSpace(protocolInfo))
        {
            return null;
        }

        var parts = protocolInfo.Split(':');
        return parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]) && parts[2] != "*"
            ? parts[2]
            : null;
    }
}
