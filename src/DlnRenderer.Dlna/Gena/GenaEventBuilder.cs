using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DlnRenderer.Dlna.Gena;

/// <summary>
/// Builds the <c>NOTIFY</c> bodies GENA subscribers receive.
/// </summary>
/// <remarks>
/// Two shapes are used by the services this renderer implements:
/// <list type="bullet">
/// <item><b>Direct variables</b> (ConnectionManager): a <c>propertyset</c> with one
/// <c>property</c> per evented variable.</item>
/// <item><b>LastChange</b> (AVTransport, RenderingControl): a single <c>LastChange</c> property whose
/// text is an <em>escaped</em> <c>&lt;Event&gt;</c> document. That indirection is what the AV
/// specifications require, and clients parse the inner XML after unescaping it.</item>
/// </list>
/// Both must carry the <c>e:</c> namespace <c>urn:schemas-upnp-org:event-1-0</c>, and every value is
/// escaped by the XML writer rather than by hand.
/// <para>
/// Inside the escaped document the event namespace is declared as the <em>default</em> namespace and
/// the variables are written without a prefix. Clients written against renderers that do this read
/// the variables by tag name; a document that only declares an <c>m:</c> prefix and writes
/// <c>&lt;m:TransportState&gt;</c> is skipped by exactly those parsers, so the phone never learns that
/// playback started and shows the cast as failed.
/// </para>
/// </remarks>
public static class GenaEventBuilder
{
    public static readonly XNamespace Event = "urn:schemas-upnp-org:event-1-0";

    public static readonly XNamespace AvTransportEvent = "urn:schemas-upnp-org:metadata-1-0/AVT/";

    public static readonly XNamespace RenderingControlEvent = "urn:schemas-upnp-org:metadata-1-0/RCS/";

    /// <summary>Wraps a list of variables in the standard <c>propertyset</c> envelope.</summary>
    public static string BuildPropertySet(IEnumerable<KeyValuePair<string, string>> variables)
    {
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Event + "propertyset",
                new XAttribute(XNamespace.Xmlns + "e", Event),
                variables.Select(variable =>
                    new XElement(Event + "property",
                        new XElement(variable.Key, variable.Value)))));

        return Serialize(document);
    }

    /// <summary>
    /// Builds a <c>LastChange</c> notification for AVTransport.
    /// </summary>
    /// <param name="state">Transport state, e.g. <c>PLAYING</c>.</param>
    /// <param name="status"><c>OK</c> or <c>ERROR_OCCURRED</c>.</param>
    /// <param name="track">Current track number, 0 when nothing is loaded.</param>
    /// <param name="numberOfTracks">Number of tracks available, 0 or 1 in this renderer.</param>
    /// <param name="duration">Track duration as <c>H:MM:SS</c>, empty when unknown.</param>
    /// <remarks>
    /// The variable set is the one casting clients actually consume: they watch
    /// <c>TransportState</c> to switch the remote control between play and pause, and read the two
    /// durations to draw the progress bar before their first <c>GetPositionInfo</c> poll. The media
    /// URI is deliberately absent - it is what <c>SetAVTransportURI</c> already sent, and clients that
    /// want it call <c>GetMediaInfo</c>.
    /// </remarks>
    public static string BuildAvTransportLastChange(
        string state,
        string status,
        int track,
        int numberOfTracks,
        string duration)
    {
        var instance = new XElement(
            AvTransportEvent + "InstanceID",
            new XAttribute("val", "0"),
            StateVariable(AvTransportEvent, "TransportState", state),
            StateVariable(AvTransportEvent, "TransportStatus", status),
            StateVariable(AvTransportEvent, "CurrentMediaDuration", duration),
            StateVariable(AvTransportEvent, "CurrentTrackDuration", duration),
            StateVariable(AvTransportEvent, "CurrentTrack", track.ToString(CultureInfo.InvariantCulture)),
            StateVariable(AvTransportEvent, "NumberOfTracks", numberOfTracks.ToString(CultureInfo.InvariantCulture)));

        return BuildLastChange(AvTransportEvent, instance);
    }

    /// <summary>Builds a <c>LastChange</c> notification for RenderingControl.</summary>
    /// <param name="volume">Player volume, 0-100.</param>
    /// <param name="muted">Player mute flag.</param>
    public static string BuildRenderingControlLastChange(int volume, bool muted)
    {
        var instance = new XElement(
            RenderingControlEvent + "InstanceID",
            new XAttribute("val", "0"),
            // channel="Master" is required by the RCS event schema; omitting it makes clients ignore
            // the whole event rather than the single variable.
            new XElement(
                RenderingControlEvent + "Volume",
                new XAttribute("channel", "Master"),
                new XAttribute("val", Clamp(volume).ToString(CultureInfo.InvariantCulture))),
            new XElement(
                RenderingControlEvent + "Mute",
                new XAttribute("channel", "Master"),
                new XAttribute("val", muted ? "1" : "0")));

        return BuildLastChange(RenderingControlEvent, instance);
    }

    /// <summary>
    /// Wraps an <c>&lt;Event&gt;</c> document into the single escaped <c>LastChange</c> property.
    /// </summary>
    /// <remarks>
    /// The inner document declares the event namespace as its default namespace (see the type
    /// remarks) and is stored as text so the outer XML writer escapes it exactly once.
    /// </remarks>
    private static string BuildLastChange(XNamespace eventNamespace, XElement instance)
    {
        // No prefix mapping is declared: the serializer then emits the namespace of the root as a
        // default declaration, which is what makes the variables appear unprefixed.
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(eventNamespace + "Event", instance));

        var lastChange = Serialize(document);

        return BuildPropertySet([new KeyValuePair<string, string>("LastChange", lastChange)]);
    }

    /// <summary>Builds <c>&lt;Name val="..."/&gt;</c> in the event namespace.</summary>
    private static XElement StateVariable(XNamespace eventNamespace, string name, string value)
        => new(eventNamespace + name, new XAttribute("val", value));

    private static int Clamp(int volume) => Math.Clamp(volume, 0, 100);

    private static string Serialize(XDocument document)
    {
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(false),
            Indent = false,
        };

        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, settings))
        {
            document.Save(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
