using System.Xml.Linq;
using DlnRenderer.Dlna.Soap;

namespace DlnRenderer.Dlna.Upnp;

public static class DlnaPaths
{
    public const string Description = "/description.xml";
    public const string AvTransportScpd = "/scpd/avtransport.xml";
    public const string ConnectionManagerScpd = "/scpd/connectionmanager.xml";
    public const string AvTransportControl = "/control/avtransport";
    public const string ConnectionManagerControl = "/control/connectionmanager";
    public const string AvTransportEvent = "/event/avtransport";
    public const string ConnectionManagerEvent = "/event/connectionmanager";
}

public static class UpnpNamespaces
{
    public static readonly XNamespace Device = "urn:schemas-upnp-org:device-1-0";
    public static readonly XNamespace Service = "urn:schemas-upnp-org:service-1-0";
}

/// <summary>Builds the UPnP device description. Only implemented services are declared.</summary>
public static class DeviceDescriptionBuilder
{
    public static string Build(DlnaOptions options)
    {
        var device = new XElement(UpnpNamespaces.Device + "device",
            Element("deviceType", Ssdp.SsdpConstants.MediaRendererDeviceType),
            Element("friendlyName", options.FriendlyName),
            Element("manufacturer", options.Manufacturer),
            Element("modelDescription", "DLNA media renderer"),
            Element("modelName", options.ModelName),
            Element("modelNumber", options.ModelNumber),
            Element("UDN", options.Udn),
            new XElement(UpnpNamespaces.Device + "serviceList",
                ServiceElement(
                    SoapNamespaces.AvTransportServiceType,
                    "urn:upnp-org:serviceId:AVTransport",
                    DlnaPaths.AvTransportScpd,
                    DlnaPaths.AvTransportControl,
                    DlnaPaths.AvTransportEvent),
                ServiceElement(
                    SoapNamespaces.ConnectionManagerServiceType,
                    "urn:upnp-org:serviceId:ConnectionManager",
                    DlnaPaths.ConnectionManagerScpd,
                    DlnaPaths.ConnectionManagerControl,
                    DlnaPaths.ConnectionManagerEvent)));

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(UpnpNamespaces.Device + "root",
                new XAttribute(XNamespace.Xmlns + "d", UpnpNamespaces.Device),
                Element("specVersion",
                    Element("major", "1"),
                    Element("minor", "0")),
                device));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement ServiceElement(string serviceType, string serviceId, string scpd, string control, string events)
        => Element("service",
            Element("serviceType", serviceType),
            Element("serviceId", serviceId),
            Element("SCPDURL", scpd),
            Element("controlURL", control),
            Element("eventSubURL", events));

    private static XElement Element(string name, params object[] content)
        => new(UpnpNamespaces.Device + name, content);

    private static XElement Element(string name, string value)
        => new(UpnpNamespaces.Device + name, value);
}

/// <summary>Service Control Protocol Documents for the two declared services.</summary>
public static class ScpdBuilder
{
    public static string BuildAvTransport()
    {
        var actions = new List<XElement>
        {
            Action("SetAVTransportURI",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                In("CurrentURI", "AVTransportURI"),
                In("CurrentURIMetaData", "AVTransportURIMetaData")),
            Action("Play",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                In("Speed", "TransportPlaySpeed")),
            Action("Pause", In("InstanceID", "A_ARG_TYPE_InstanceID")),
            Action("Stop", In("InstanceID", "A_ARG_TYPE_InstanceID")),
            Action("Seek",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                In("Unit", "A_ARG_TYPE_SeekMode"),
                In("Target", "A_ARG_TYPE_SeekTarget")),
            Action("GetTransportInfo",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                Out("CurrentTransportState", "TransportState"),
                Out("CurrentTransportStatus", "TransportStatus"),
                Out("CurrentSpeed", "TransportPlaySpeed")),
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
            Action("GetDeviceCapabilities",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                Out("PlayMedia", "PossiblePlaybackStorageMedia"),
                Out("RecMedia", "PossibleRecordStorageMedia"),
                Out("RecQualityModes", "PossibleRecordQualityModes")),
            Action("GetTransportSettings",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                Out("PlayMode", "CurrentPlayMode"),
                Out("RecQualityMode", "CurrentRecordQualityMode")),
            Action("GetCurrentTransportActions",
                In("InstanceID", "A_ARG_TYPE_InstanceID"),
                Out("Actions", "CurrentTransportActions")),
        };

        var variables = new List<XElement>
        {
            Variable("TransportState", "string", false, ["STOPPED", "PLAYING", "PAUSED_PLAYBACK", "TRANSITIONING"]),
            Variable("TransportStatus", "string", false, ["OK", "ERROR_OCCURRED"]),
            Variable("TransportPlaySpeed", "string", false, ["1"]),
            Variable("AVTransportURI", "string", false),
            Variable("AVTransportURIMetaData", "string", false),
            Variable("NextAVTransportURI", "string", false),
            Variable("NextAVTransportURIMetaData", "string", false),
            Variable("CurrentTrack", "ui4", false),
            Variable("CurrentTrackDuration", "string", false),
            Variable("CurrentTrackMetaData", "string", false),
            Variable("CurrentTrackURI", "string", false),
            Variable("CurrentMediaDuration", "string", false),
            Variable("NumberOfTracks", "ui4", false),
            Variable("RelativeTimePosition", "string", false),
            Variable("AbsoluteTimePosition", "string", false),
            Variable("RelativeCounterPosition", "i4", false),
            Variable("AbsoluteCounterPosition", "i4", false),
            Variable("CurrentTransportActions", "string", false),
            Variable("PlaybackStorageMedium", "string", false, ["NONE", "NETWORK"]),
            Variable("RecordStorageMedium", "string", false, ["NOT_IMPLEMENTED"]),
            Variable("PossiblePlaybackStorageMedia", "string", false),
            Variable("PossibleRecordStorageMedia", "string", false),
            Variable("RecordMediumWriteStatus", "string", false, ["NOT_IMPLEMENTED"]),
            Variable("PossibleRecordQualityModes", "string", false),
            Variable("CurrentRecordQualityMode", "string", false, ["NOT_IMPLEMENTED"]),
            Variable("CurrentPlayMode", "string", false, ["NORMAL"]),
            Variable("A_ARG_TYPE_InstanceID", "ui4", false),
            Variable("A_ARG_TYPE_SeekMode", "string", false, ["REL_TIME", "ABS_TIME"]),
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
            Variable("SourceProtocolInfo", "string", false),
            Variable("SinkProtocolInfo", "string", false),
            Variable("CurrentConnectionIDs", "string", false),
            Variable("A_ARG_TYPE_ConnectionStatus", "string", false, ["OK", "Unknown"]),
            Variable("A_ARG_TYPE_ConnectionManager", "string", false),
            Variable("A_ARG_TYPE_Direction", "string", false, ["Input", "Output"]),
            Variable("A_ARG_TYPE_ProtocolInfo", "string", false),
            Variable("A_ARG_TYPE_ConnectionID", "i4", false),
            Variable("A_ARG_TYPE_AVTransportID", "i4", false),
            Variable("A_ARG_TYPE_RcsID", "i4", false),
        };

        return BuildScpd(actions, variables);
    }

    private static string BuildScpd(IEnumerable<XElement> actions, IEnumerable<XElement> variables)
    {
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(UpnpNamespaces.Service + "scpd",
                new XAttribute(XNamespace.Xmlns + "s", UpnpNamespaces.Service),
                new XElement(UpnpNamespaces.Service + "specVersion",
                    new XElement(UpnpNamespaces.Service + "major", "1"),
                    new XElement(UpnpNamespaces.Service + "minor", "0")),
                new XElement(UpnpNamespaces.Service + "actionList", actions),
                new XElement(UpnpNamespaces.Service + "serviceStateTable", variables)));

        return document.ToString(SaveOptions.DisableFormatting);
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

    private static XElement Variable(string name, string dataType, bool sendEvents, string[]? allowedValues = null)
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

        return variable;
    }
}
