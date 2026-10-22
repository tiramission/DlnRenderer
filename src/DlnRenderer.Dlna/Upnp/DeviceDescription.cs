using System.Text;
using System.Xml;
using System.Xml.Linq;
using DlnRenderer.Dlna.Soap;

namespace DlnRenderer.Dlna.Upnp;

public static class DlnaPaths
{
    public const string Description = "/description.xml";
    public const string AvTransportScpd = "/scpd/avtransport.xml";
    public const string ConnectionManagerScpd = "/scpd/connectionmanager.xml";
    public const string RenderingControlScpd = "/scpd/renderingcontrol.xml";
    public const string AvTransportControl = "/control/avtransport";
    public const string ConnectionManagerControl = "/control/connectionmanager";
    public const string RenderingControlControl = "/control/renderingcontrol";
    public const string AvTransportEvent = "/event/avtransport";
    public const string ConnectionManagerEvent = "/event/connectionmanager";
    public const string RenderingControlEvent = "/event/renderingcontrol";
}

public static class UpnpNamespaces
{
    public static readonly XNamespace Device = "urn:schemas-upnp-org:device-1-0";
    public static readonly XNamespace Service = "urn:schemas-upnp-org:service-1-0";

    /// <summary>
    /// DLNA device-profile namespace. Control points look for <c>dlna:X_DLNADOC</c> to decide
    /// whether a device is a renderer they may target.
    /// </summary>
    public static readonly XNamespace Dlna = "urn:schemas-dlna-org:device-1-0";
}

/// <summary>Builds the UPnP device description. Only implemented services are declared.</summary>
/// <remarks>
/// The document uses the default (unprefixed) namespace for the device elements, matching the
/// renderers that vendor casting clients are known to work with. Emitting the same elements with a
/// <c>d:</c> prefix is spec-valid, but clients that compare element names textually - rather than
/// resolving namespaces - then fail to recognise the device even though discovery succeeded.
/// <para>
/// Field set mirrors the reference renderer that Chinese casting apps (Douyin, Tencent Video) are
/// verified against: it carries manufacturerURL, modelURL and serialNumber, and casting client
/// device lists read those to build the entry they show the user. A missing element is not an XML
/// error, but a client that indexes into a fixed field list can drop the device.
/// </para>
/// </remarks>
public static class DeviceDescriptionBuilder
{
    /// <summary>
    /// DLNA device profile. <c>DMR-1.50</c> declares the standard Digital Media Renderer role;
    /// it is a capability marker required by DLNA-aware clients, not a certification claim.
    /// </summary>
    public const string DlnaDeviceProfile = "DMR-1.50";

    public static string Build(DlnaOptions options, string? urlBase = null)
    {
        // Elements are created unqualified and then given the default namespace below; this keeps
        // the serialized form free of prefixes.
        XElement Device(string name, params object[] content) => new(name, content);

        var device = Device("device",
            Device("deviceType", Ssdp.SsdpConstants.MediaRendererDeviceType),
            Device("UDN", options.Udn),
            Device("friendlyName", options.FriendlyName),
            Device("manufacturer", options.Manufacturer),
            Device("manufacturerURL", options.ManufacturerUrl),
            Device("modelDescription", "AVTransport Media Renderer"),
            Device("modelName", options.ModelName),
            Device("modelNumber", options.ModelNumber),
            Device("modelURL", options.ModelUrl),
            Device("serialNumber", options.SerialNumber),
            // Declared with its own xmlns:dlna exactly as DLNA clients expect to find it.
            new XElement("X_DLNADOC",
                new XAttribute(XNamespace.Xmlns + "dlna", UpnpNamespaces.Dlna),
                DlnaDeviceProfile),
            Device("serviceList",
                ServiceElement(
                    SoapNamespaces.AvTransportServiceType,
                    "urn:upnp-org:serviceId:AVTransport",
                    DlnaPaths.AvTransportScpd,
                    DlnaPaths.AvTransportControl,
                    DlnaPaths.AvTransportEvent),
                ServiceElement(
                    SoapNamespaces.RenderingControlServiceType,
                    "urn:upnp-org:serviceId:RenderingControl",
                    DlnaPaths.RenderingControlScpd,
                    DlnaPaths.RenderingControlControl,
                    DlnaPaths.RenderingControlEvent),
                ServiceElement(
                    SoapNamespaces.ConnectionManagerServiceType,
                    "urn:upnp-org:serviceId:ConnectionManager",
                    DlnaPaths.ConnectionManagerScpd,
                    DlnaPaths.ConnectionManagerControl,
                    DlnaPaths.ConnectionManagerEvent)));

        _ = urlBase;

        var root = Device("root",
            new XAttribute(XNamespace.Xmlns + "dlna", UpnpNamespaces.Dlna),
            Device("specVersion",
                Device("major", "1"),
                Device("minor", "0")),
            device);

        // Apply the UPnP device namespace as the default namespace for every unqualified element.
        foreach (var element in root.DescendantsAndSelf().Where(e => e.Name.Namespace == XNamespace.None))
        {
            element.Name = UpnpNamespaces.Device + element.Name.LocalName;
        }

        // X_DLNADOC belongs to the DLNA namespace; re-qualify it after the loop above.
        var dlnaDoc = root.Descendants().Single(e => e.Name.LocalName == "X_DLNADOC");
        dlnaDoc.Name = UpnpNamespaces.Dlna + "X_DLNADOC";
        dlnaDoc.SetAttributeValue(XNamespace.Xmlns + "dlna", null);

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);

        return SerializeWithDeclaration(document);
    }

    private static XElement ServiceElement(string serviceType, string serviceId, string scpd, string control, string events)
        => new("service",
            new XElement("serviceType", serviceType),
            new XElement("serviceId", serviceId),
            new XElement("controlURL", control.TrimStart('/')),
            new XElement("eventSubURL", events.TrimStart('/')),
            new XElement("SCPDURL", scpd.TrimStart('/')));

    /// <summary>
    /// Serializes with an explicit XML declaration. <see cref="XDocument.ToString()"/> drops the
    /// declaration entirely, and some casting clients reject a description that lacks one.
    /// </summary>
    internal static string SerializeWithDeclaration(XDocument document)
    {
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(false),
        };

        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, settings))
        {
            document.Save(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>
/// Builds the three service descriptions (SCPDs).
/// </summary>
/// <remarks>
/// Two properties of these documents are load-bearing for real casting clients, and both were the
/// cause of devices being discovered but not offered as a cast target:
/// <list type="bullet">
/// <item>
/// The root element is serialized in the <em>default</em> namespace. Writing it as
/// <c>&lt;s:scpd xmlns:s="..."&gt;</c> is equally valid XML, but clients that walk the document by
/// tag name (rather than by namespace URI) then find no actions at all and conclude the renderer
/// supports nothing.
/// </item>
/// <item>
/// The action and state-variable tables are complete, including actions this renderer treats as
/// no-ops (<c>SetPlayMode</c>, <c>Next</c>, <c>Previous</c>). Casting apps read the table to decide
/// which requests they may send; an action missing from the table is treated as a device that
/// cannot do it, even though a SOAP call would otherwise have been answered.
/// </item>
/// </list>
/// </remarks>
public static class ScpdBuilder
{
    public static string BuildAvTransport()
    {
        var actions = new List<XElement>
        {
            Action("GetCurrentTransportActions",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                Out("Actions", "CurrentTransportActions")),
            Action("GetDeviceCapabilities",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                Out("PlayMedia", "PossiblePlaybackStorageMedia"),
                Out("RecMedia", "PossibleRecordStorageMedia"),
                Out("RecQualityModes", "PossibleRecordQualityModes")),
            Action("GetMediaInfo",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                Out("NrTracks", "NumberOfTracks"),
                Out("MediaDuration", "CurrentMediaDuration"),
                Out("CurrentURI", "AVTransportURI"),
                Out("CurrentURIMetaData", "AVTransportURIMetaData"),
                Out("NextURI", "NextAVTransportURI"),
                Out("NextURIMetaData", "NextAVTransportURIMetaData"),
                Out("PlayMedium", "PlaybackStorageMedium"),
                Out("RecordMedium", "RecordStorageMedium"),
                Out("WriteStatus", "RecordMediumWriteStatus")),
            Action("GetPositionInfo",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                Out("Track", "CurrentTrack"),
                Out("TrackDuration", "CurrentTrackDuration"),
                Out("TrackMetaData", "CurrentTrackMetaData"),
                Out("TrackURI", "CurrentTrackURI"),
                Out("RelTime", "RelativeTimePosition"),
                Out("AbsTime", "AbsoluteTimePosition"),
                Out("RelCount", "RelativeCounterPosition"),
                Out("AbsCount", "AbsoluteCounterPosition")),
            Action("GetTransportInfo",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                Out("CurrentTransportState", "TransportState"),
                Out("CurrentTransportStatus", "TransportStatus"),
                Out("CurrentSpeed", "TransportPlaySpeed")),
            Action("GetTransportSettings",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                Out("PlayMode", "CurrentPlayMode"),
                Out("RecQualityMode", "CurrentRecordQualityMode")),
            Action("Next", In("InstanceID", "A_ARG_TYPE_InstanceID")),
            Action("Pause", In("InstanceID", "A_ARG_TYPE_InstanceID")),
            Action("Play",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                In("Speed", "TransportPlaySpeed")),
            Action("Previous", In("InstanceID", "A_ARG_TYPE_InstanceID")),
            Action("Seek",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                In("Unit", "A_ARG_TYPE_SeekMode"),
                In("Target", "A_ARG_TYPE_SeekTarget")),
            Action("SetAVTransportURI",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                In("CurrentURI", "AVTransportURI"),
                In("CurrentURIMetaData", "AVTransportURIMetaData")),
            Action("SetPlayMode",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                In("NewPlayMode", "CurrentPlayMode")),
            Action("Stop", In("InstanceID", "A_ARG_TYPE_InstanceID")),
        };

        var variables = new List<XElement>
        {
            Variable("TransportState", "string", false,
                ["STOPPED", "PAUSED_PLAYBACK", "PLAYING", "TRANSITIONING", "NO_MEDIA_PRESENT"]),
            Variable("TransportStatus", "string", false, ["OK", "ERROR_OCCURRED"]),
            Variable("TransportPlaySpeed", "string", false, ["0.5", "0.75", "1", "1.25", "1.5", "2"]),
            Variable("AVTransportURI", "string", false),
            Variable("AVTransportURIMetaData", "string", false),
            Variable("NextAVTransportURI", "string", false),
            Variable("NextAVTransportURIMetaData", "string", false),
            Variable("CurrentTrack", "ui4", false, max: "65535", min: "0", step: "1"),
            Variable("CurrentTrackDuration", "string", false),
            Variable("CurrentTrackMetaData", "string", false),
            Variable("CurrentTrackURI", "string", false),
            Variable("CurrentTrackTitle", "string", false),
            Variable("CurrentMediaDuration", "string", false),
            Variable("NumberOfTracks", "ui4", false, min: "0", max: "65535"),
            Variable("RelativeTimePosition", "string", false),
            Variable("AbsoluteTimePosition", "string", false),
            Variable("RelativeCounterPosition", "i4", false),
            Variable("AbsoluteCounterPosition", "i4", false),
            Variable("CurrentTransportActions", "string", false),
            Variable("PlaybackStorageMedium", "string", false, ["NONE", "UNKNOWN", "CD-DA", "HDD", "NETWORK"]),
            Variable("PossiblePlaybackStorageMedia", "string", false, ["NONE", "UNKNOWN", "CD-DA", "HDD", "NETWORK"]),
            Variable("RecordStorageMedium", "string", false, ["NOT_IMPLEMENTED"]),
            Variable("PossibleRecordStorageMedia", "string", false, ["NOT_IMPLEMENTED"]),
            Variable("RecordMediumWriteStatus", "string", false, ["NOT_IMPLEMENTED"]),
            Variable("PossibleRecordQualityModes", "string", false, ["NOT_IMPLEMENTED"]),
            Variable("CurrentRecordQualityMode", "string", false, ["NOT_IMPLEMENTED"]),
            Variable("CurrentPlayMode", "string", false, ["NORMAL", "REPEAT_ONE", "REPEAT_ALL", "SHUFFLE", "SHUFFLE_NOREPEAT"]),
            Variable("DisplayCurrentSubtitle", "boolean", false),
            // AVTransport eventing is carried by LastChange, whose value is the escaped Event XML.
            // The individual variables stay non-evented: the AV specifications require exactly this
            // shape, and clients that receive a bare TransportState notification ignore it.
            Variable("LastChange", "string", true),
            Variable("A_ARG_TYPE_InstanceID", "ui4", false),
            Variable("A_ARG_TYPE_SeekMode", "string", false, ["REL_TIME", "ABS_TIME", "TRACK_NR"]),
            Variable("A_ARG_TYPE_SeekTarget", "string", false),
        };

        return BuildScpd(actions, variables);
    }

    public static string BuildConnectionManager()
    {
        var actions = new List<XElement>
        {
            Action("GetProtocolInfo",
                Out("Source", "SourceProtocolInfo"),
                Out("Sink", "SinkProtocolInfo")),
            Action("GetCurrentConnectionIDs",
                Out("ConnectionIDs", "CurrentConnectionIDs")),
            Action("GetCurrentConnectionInfo",
                In("ConnectionID", "A_ARG_TYPE_ConnectionID"),
                Out("RcsID", "A_ARG_TYPE_RcsID"),
                Out("AVTransportID", "A_ARG_TYPE_AVTransportID"),
                Out("ProtocolInfo", "A_ARG_TYPE_ProtocolInfo"),
                Out("PeerConnectionManager", "A_ARG_TYPE_ConnectionManager"),
                Out("PeerConnectionID", "A_ARG_TYPE_ConnectionID"),
                Out("Direction", "A_ARG_TYPE_Direction"),
                Out("Status", "A_ARG_TYPE_ConnectionStatus")),
        };

        var variables = new List<XElement>
        {
            // The three variables are evented exactly as the reference renderer declares them, so a
            // client that subscribes before casting receives the sink list in the initial event
            // instead of having to call GetProtocolInfo first.
            Variable("SourceProtocolInfo", "string", true),
            Variable("SinkProtocolInfo", "string", true),
            Variable("CurrentConnectionIDs", "string", true),
            Variable("A_ARG_TYPE_ConnectionStatus", "string", false,
                ["OK", "ContentFormatMismatch", "InsufficientBandwidth", "UnreliableChannel", "Unknown"]),
            Variable("A_ARG_TYPE_ConnectionManager", "string", false),
            Variable("A_ARG_TYPE_Direction", "string", false, ["Input", "Output"]),
            Variable("A_ARG_TYPE_ProtocolInfo", "string", false),
            Variable("A_ARG_TYPE_ConnectionID", "i4", false),
            Variable("A_ARG_TYPE_AVTransportID", "i4", false),
            Variable("A_ARG_TYPE_RcsID", "i4", false),
        };

        return BuildScpd(actions, variables);
    }

    public static string BuildRenderingControl()
    {
        var actions = new List<XElement>
        {
            Action("GetMute",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                In("Channel", "A_ARG_TYPE_Channel"),
                Out("CurrentMute", "Mute")),
            Action("GetVolume",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                In("Channel", "A_ARG_TYPE_Channel"),
                Out("CurrentVolume", "Volume")),
            Action("GetVolumeDB",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                In("Channel", "A_ARG_TYPE_Channel"),
                Out("CurrentVolume", "VolumeDB")),
            Action("GetVolumeDBRange",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                In("Channel", "A_ARG_TYPE_Channel"),
                Out("MinValue", "VolumeDB"),
                Out("MaxValue", "VolumeDB")),
            Action("ListPresets",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                Out("CurrentPresetNameList", "PresetNameList")),
            Action("SelectPreset",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                In("PresetName", "A_ARG_TYPE_PresetName")),
            Action("SetMute",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                In("Channel", "A_ARG_TYPE_Channel"),
                In("DesiredMute", "Mute")),
            Action("SetVolume",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                In("Channel", "A_ARG_TYPE_Channel"),
                In("DesiredVolume", "Volume")),
        };

        var variables = new List<XElement>
        {
            Variable("PresetNameList", "string", false),
            Variable("LastChange", "string", true),
            Variable("Mute", "boolean", false),
            Variable("Volume", "ui2", false, min: "0", max: "100", step: "1"),
            Variable("VolumeDB", "i2", false, min: "-32767", max: "32767"),
            Variable("A_ARG_TYPE_Channel", "string", false, ["Master"]),
            Variable("A_ARG_TYPE_InstanceID", "ui4", false),
            Variable("A_ARG_TYPE_PresetName", "string", false, ["FactoryDefaults"]),
        };

        return BuildScpd(actions, variables);
    }

    private static string BuildScpd(IEnumerable<XElement> actions, IEnumerable<XElement> variables)
    {
        // No xmlns:s prefix: see the type remarks. Child elements already carry the service
        // namespace, so declaring it as the default on the root produces unprefixed tags.
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(UpnpNamespaces.Service + "scpd",
                new XElement(UpnpNamespaces.Service + "specVersion",
                    new XElement(UpnpNamespaces.Service + "major", "1"),
                    new XElement(UpnpNamespaces.Service + "minor", "0")),
                new XElement(UpnpNamespaces.Service + "actionList", actions),
                new XElement(UpnpNamespaces.Service + "serviceStateTable", variables)));

        // The declaration is emitted explicitly: the reference renderer's SCPDs carry one, and an
        // SCPD fetched with a plain XML parser behaves identically either way, so matching it costs
        // nothing and removes a difference a client could branch on.
        return DeviceDescriptionBuilder.SerializeWithDeclaration(document);
    }

    private static XElement Action(string name, params XElement[] arguments)
    {
        var action = new XElement(UpnpNamespaces.Service + "action",
            new XElement(UpnpNamespaces.Service + "name", name));

        if (arguments.Length > 0)
        {
            action.Add(new XElement(UpnpNamespaces.Service + "argumentList", arguments));
        }

        return action;
    }

    private static XElement In(string name, string relatedStateVariable)
        => Argument(name, "in", relatedStateVariable);

    private static XElement Out(string name, string relatedStateVariable)
        => Argument(name, "out", relatedStateVariable);

    private static XElement Argument(string name, string direction, string relatedStateVariable)
        => new(UpnpNamespaces.Service + "argument",
            new XElement(UpnpNamespaces.Service + "name", name),
            new XElement(UpnpNamespaces.Service + "direction", direction),
            new XElement(UpnpNamespaces.Service + "relatedStateVariable", relatedStateVariable));

    private static XElement Variable(
        string name,
        string dataType,
        bool sendEvents,
        string[]? allowedValues = null,
        string? min = null,
        string? max = null,
        string? step = null)
    {
        var variable = new XElement(UpnpNamespaces.Service + "stateVariable",
            new XAttribute("sendEvents", sendEvents ? "yes" : "no"),
            new XElement(UpnpNamespaces.Service + "name", name),
            new XElement(UpnpNamespaces.Service + "dataType", dataType));

        if (allowedValues is not null)
        {
            variable.Add(new XElement(UpnpNamespaces.Service + "allowedValueList",
                allowedValues.Select(v => new XElement(UpnpNamespaces.Service + "allowedValue", v))));
        }

        if (min is not null || max is not null)
        {
            var range = new XElement(UpnpNamespaces.Service + "allowedValueRange");
            if (min is not null)
            {
                range.Add(new XElement(UpnpNamespaces.Service + "minimum", min));
            }

            if (max is not null)
            {
                range.Add(new XElement(UpnpNamespaces.Service + "maximum", max));
            }

            if (step is not null)
            {
                range.Add(new XElement(UpnpNamespaces.Service + "step", step));
            }

            variable.Add(range);
        }

        return variable;
    }
}
