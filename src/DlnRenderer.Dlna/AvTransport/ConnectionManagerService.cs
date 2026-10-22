using DlnRenderer.Dlna.Soap;

namespace DlnRenderer.Dlna.AvTransport;

/// <summary>
/// ConnectionManager:1 as required by control points to negotiate a transfer protocol.
/// </summary>
/// <remarks>
/// The sink list is an explicit enumeration of the container/codec combinations the renderer
/// accepts, not a wildcard. Control points negotiate against it before pushing media: with a
/// wildcard-only list many of them conclude the renderer accepts nothing and keep the device out of
/// their casting menus, even though SSDP discovery succeeded.
/// </remarks>
public sealed class ConnectionManagerService
{
    /// <summary>Renderer does not source any content.</summary>
    private const string SourceProtocolInfo = "";

    /// <summary>
    /// The advertised sink list. Also used for the service's evented <c>SinkProtocolInfo</c> variable,
    /// so a subscribing control point and a polling one see exactly the same capabilities.
    /// </summary>
    internal static string SinkProtocolInfo { get; } = LoadSinkProtocolInfo();

    public string Handle(SoapRequest request)
    {
        switch (request.Action)
        {
            case "GetProtocolInfo":
                return Respond(request,
                [
                    new("Source", SourceProtocolInfo),
                    new("Sink", SinkProtocolInfo),
                ]);

            case "GetCurrentConnectionIDs":
                return Respond(request, [new("ConnectionIDs", "0")]);

            case "GetCurrentConnectionInfo":
                var id = request.Argument("ConnectionID");
                if (!int.TryParse(id?.Trim(), out var connectionId))
                {
                    throw new UpnpException(UpnpErrorCode.InvalidArgs, "ConnectionID is required and must be an integer");
                }

                if (connectionId != 0)
                {
                    throw new UpnpException(706, "Invalid connection reference");
                }

                return Respond(request,
                [
                    new("RcsID", "-1"),
                    new("AVTransportID", "0"),
                    new("ProtocolInfo", SinkProtocolInfo),
                    new("PeerConnectionManager", string.Empty),
                    new("PeerConnectionID", "-1"),
                    new("Direction", "Input"),
                    new("Status", "OK"),
                ]);

            default:
                throw new UpnpException(UpnpErrorCode.InvalidAction, $"unsupported action {request.Action}");
        }
    }

    /// <summary>
    /// Reads the advertised sink list from the embedded resource. Falling back to a wildcard would
    /// silently reintroduce the "visible but unplayable" behaviour, so a missing resource is treated
    /// as a packaging error and surfaces immediately.
    /// </summary>
    private static string LoadSinkProtocolInfo()
    {
        const string resourceName = "DlnRenderer.Dlna.SinkProtocolInfo.csv";
        using var stream = typeof(ConnectionManagerService).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' is missing; the renderer cannot advertise its media formats.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    private static string Respond(SoapRequest request, IReadOnlyList<KeyValuePair<string, string>> arguments)
        => SoapXml.BuildResponse(SoapNamespaces.ConnectionManagerServiceType, request.Action, arguments);
}
