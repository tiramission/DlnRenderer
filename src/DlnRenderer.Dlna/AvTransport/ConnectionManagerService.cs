using DlnRenderer.Dlna.Soap;

namespace DlnRenderer.Dlna.AvTransport;

/// <summary>
/// ConnectionManager:1 as required by control points to negotiate a transfer protocol. This
/// renderer only accepts http-get input, so it declares exactly that as a sink and nothing else.
/// </summary>
public sealed class ConnectionManagerService
{
    /// <summary>Renderer does not source any content.</summary>
    private const string SourceProtocolInfo = "";

    private const string SinkProtocolInfo = "http-get:*:*:*";

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

    private static string Respond(SoapRequest request, IReadOnlyList<KeyValuePair<string, string>> arguments)
        => SoapXml.BuildResponse(SoapNamespaces.ConnectionManagerServiceType, request.Action, arguments);
}
