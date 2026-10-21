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
    /// <param name="state">Transport state as advertised in <c>TransportState</c>.</param>
    /// <param name="status"><c>OK</c> or <c>ERROR_OCCURRED</c>.</param>
    /// <param name="currentUri">Current media URI, empty when nothing is loaded.</param>
    /// <param name="duration">Track duration as <c>H:MM:SS</c>, empty when unknown.</param>
    /// <param name="actions">Actions currently available, comma separated.</param>
    public static string BuildAvTransportLastChange(
        string state,
        string status,
        string currentUri,
        string duration,
        string actions)
    {
        var instance = new XElement(
            AvTransportEvent + "InstanceID",
            new XAttribute("val", "0"),
            StateVariable(AvTransportEvent, "TransportState", state),
            StateVariable(AvTransportEvent, "TransportStatus", status),
            StateVariable(AvTransportEvent, "CurrentTrackURI", currentUri),
            StateVariable(AvTransportEvent, "CurrentTrackDuration", duration),
            StateVariable(AvTransportEvent, "CurrentMediaDuration", duration),
            StateVariable(AvTransportEvent, "CurrentTransportActions", actions));

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
    /// The inner document is serialised with the event namespace as its default namespace and the
    /// <c>xmlns:m</c> prefix the AV specifications use, then stored as text so the outer XML writer
    /// escapes it exactly once.
    /// </remarks>
    private static string BuildLastChange(XNamespace eventNamespace, XElement instance)
    {
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                eventNamespace + "Event",
                new XAttribute(XNamespace.Xmlns + "e", eventNamespace),
                new XAttribute(XNamespace.Xmlns + "m", eventNamespace),
                instance));

        var lastChange = Serialize(document);

        return BuildPropertySet([new KeyValuePair<string, string>("LastChange", lastChange)]);
    }

    /// <summary>
    /// Builds <c>&lt;m:Name val="..."/&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The element must be created in the event namespace, not named "m:Name": XName treats a colon
    /// as part of a local name and throws. The serializer then emits the m: prefix declared on the
    /// Event element.
    /// </remarks>
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
