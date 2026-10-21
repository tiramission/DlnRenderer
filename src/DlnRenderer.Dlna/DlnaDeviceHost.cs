using System.Net;
using DlnRenderer.Dlna.AvTransport;
using DlnRenderer.Dlna.Gena;
using DlnRenderer.Dlna.Http;
using DlnRenderer.Dlna.Soap;
using DlnRenderer.Dlna.Ssdp;
using DlnRenderer.Dlna.Upnp;

namespace DlnRenderer.Dlna;

/// <summary>
/// Owns the network side of the renderer: the HTTP endpoint (description, SCPDs, SOAP control,
/// GENA subscriptions) and the SSDP responder/announcer. Protocol handling itself lives in the
/// services, so this type only routes requests and manages lifetimes.
/// </summary>
public sealed class DlnaDeviceHost : IAsyncDisposable
{
    private readonly DlnaOptions _options;
    private readonly ITransportRenderer _renderer;
    private readonly Action<string> _log;
    private readonly AvTransportService _avTransport;
    private readonly ConnectionManagerService _connectionManager = new();
    private readonly RenderingControl.RenderingControlService _renderingControl;
    private readonly GenaEventing _eventing;

    private SimpleHttpServer? _http;
    private SsdpServer? _ssdp;
    private bool _disposed;

    public DlnaDeviceHost(DlnaOptions options, ITransportRenderer renderer, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(renderer);

        _options = options;
        _renderer = renderer;
        _log = log ?? (_ => { });
        _avTransport = new AvTransportService(renderer, _log);
        _renderingControl = new RenderingControl.RenderingControlService(renderer, _log);
        _eventing = new GenaEventing(options.Eventing, renderer, _log);
    }

    public bool IsRunning => _http is not null;

    public string? DescriptionUrl { get; private set; }

    public string? ControlUrl { get; private set; }

    public int HttpPort => _http?.Port ?? 0;

    public bool SsdpActive => _ssdp?.IsRunning == true;

    public IReadOnlyList<IPAddress> SsdpAddresses => _ssdp?.BoundAddresses ?? [];

    /// <summary>Eventing subsystem. The service pushes player state changes through it.</summary>
    public GenaEventing Eventing => _eventing;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_http is not null)
        {
            throw new InvalidOperationException("The DLNA host is already running.");
        }

        // Bind every interface: control points reach us on whichever NIC they discovered.
        // The advertised LOCATION is chosen per SSDP socket below.
        var address = _options.ListenAddress ?? IPAddress.Any;
        var http = new SimpleHttpServer(address, _options.HttpPort, HandleRequestAsync);
        http.Start();

        var advertised = _options.ListenAddress ?? SelectAddress();
        var baseUrl = $"http://{advertised}:{http.Port}";
        _avTransportScpd = ScpdBuilder.BuildAvTransport();
        _connectionManagerScpd = ScpdBuilder.BuildConnectionManager();
        _renderingControlScpd = ScpdBuilder.BuildRenderingControl();

        _http = http;
        DescriptionUrl = baseUrl + DlnaPaths.Description;
        ControlUrl = baseUrl + DlnaPaths.AvTransportControl;
        _log($"http listening on {address}:{http.Port}");

        if (_options.EnableSsdp)
        {
            var ssdp = new SsdpServer(
                _options,
                localAddress => $"http://{localAddress}:{http.Port}{DlnaPaths.Description}",
                _log);
            try
            {
                ssdp.Start();
                _ssdp = ssdp;
            }
            catch
            {
                ssdp.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }
        }
    }

    private string _avTransportScpd = string.Empty;
    private string _connectionManagerScpd = string.Empty;
    private string _renderingControlScpd = string.Empty;

    /// <summary>
    /// Builds the device description. Service URLs are relative, so the document stays valid whichever
    /// interface the control point used to reach it.
    /// </summary>
    private string BuildDescription(DlnHttpRequest request)
    {
        string? urlBase = null;
        if (request.Headers.TryGetValue("Host", out var host) && !string.IsNullOrWhiteSpace(host))
        {
            // Only trust a syntactically valid authority; never reflect arbitrary header content.
            if (Uri.TryCreate($"http://{host.Trim()}/", UriKind.Absolute, out var parsed)
                && parsed.Port > 0)
            {
                urlBase = $"http://{parsed.Host}:{parsed.Port}";
            }
        }

        return DeviceDescriptionBuilder.Build(_options, urlBase);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ssdp is not null)
        {
            await _ssdp.DisposeAsync().ConfigureAwait(false);
            _ssdp = null;
        }

        if (_http is not null)
        {
            await _http.DisposeAsync().ConfigureAwait(false);
            _http = null;
        }

        // After the HTTP endpoint closed: no subscriber can arrive while eventing shuts down, and
        // pending notifications are cancelled rather than left to outlive the service.
        await _eventing.DisposeAsync().ConfigureAwait(false);
    }

    private IPAddress SelectAddress()
    {
        if (_options.ListenAddress is not null)
        {
            return _options.ListenAddress;
        }

        var addresses = NetworkInterfaces.GetLanAddresses();
        return addresses.Count > 0 ? addresses[0] : IPAddress.Loopback;
    }

    private async Task<DlnHttpResponse> HandleRequestAsync(DlnHttpRequest request, CancellationToken cancellationToken)
    {
        // Every control-point interaction is logged: this is what distinguishes a client that
        // never fetched the description from one that fetched it and rejected the result.
        _log($"http {request.Method} {request.Path} from {request.RemoteAddress ?? "?"}");
        try
        {
            return request.Path switch
            {
                DlnaPaths.Description => Get(request, BuildDescription(request)),
                DlnaPaths.AvTransportScpd => Get(request, _avTransportScpd),
                DlnaPaths.ConnectionManagerScpd => Get(request, _connectionManagerScpd),
                DlnaPaths.RenderingControlScpd => Get(request, _renderingControlScpd),
                DlnaPaths.AvTransportControl => await HandleSoapAsync(
                    request,
                    r => _avTransport.HandleAsync(r, cancellationToken),
                    cancellationToken).ConfigureAwait(false),
                DlnaPaths.ConnectionManagerControl => await HandleSoapAsync(
                    request,
                    r => Task.FromResult(_connectionManager.Handle(r)),
                    cancellationToken).ConfigureAwait(false),
                DlnaPaths.RenderingControlControl => await HandleSoapAsync(
                    request,
                    r => _renderingControl.HandleAsync(r, cancellationToken),
                    cancellationToken).ConfigureAwait(false),
                DlnaPaths.AvTransportEvent => await HandleEventAsync(
                    GenaServiceKind.AvTransport, request, cancellationToken).ConfigureAwait(false),
                DlnaPaths.RenderingControlEvent => await HandleEventAsync(
                    GenaServiceKind.RenderingControl, request, cancellationToken).ConfigureAwait(false),
                DlnaPaths.ConnectionManagerEvent => await HandleEventAsync(
                    GenaServiceKind.ConnectionManager, request, cancellationToken).ConfigureAwait(false),
                _ => DlnHttpResponse.Text(404, "not found"),
            };
        }
        catch (UpnpException ex)
        {
            _log($"soap fault {ex.Code}: {ex.Description}");
            return FaultResponse(ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"control request failed: {ex.Message}");
            return FaultResponse(new UpnpException(UpnpErrorCode.ActionFailed, "action failed"));
        }
    }

    private async Task<DlnHttpResponse> HandleSoapAsync(
        DlnHttpRequest request,
        Func<SoapRequest, Task<string>> handler,
        CancellationToken cancellationToken)
    {
        if (request.Method != "POST")
        {
            return DlnHttpResponse.Text(405, "SOAP control requires POST");
        }

        request.Headers.TryGetValue("SOAPAction", out var soapAction);
        var parsed = SoapXml.Parse(request.Body, soapAction);

        // Log the action name: polling loops make "POST /control/avtransport" alone useless for
        // telling a control point that never sets a URI apart from one that does.
        _log($"soap action {parsed.Action} from {request.RemoteAddress ?? "?"}");
        var response = await handler(parsed).ConfigureAwait(false);
        _ = cancellationToken;
        return DlnHttpResponse.Soap(response);
    }

    /// <summary>
    /// Routes SUBSCRIBE / UNSUBSCRIBE on a service event endpoint.
    /// </summary>
    /// <remarks>
    /// Only these two methods are legal here. NOTIFY is what a device sends, never what it receives,
    /// so anything else is answered <c>405</c> instead of being treated as a control request.
    /// </remarks>
    private Task<DlnHttpResponse> HandleEventAsync(
        GenaServiceKind service,
        DlnHttpRequest request,
        CancellationToken cancellationToken)
        => request.Method switch
        {
            "SUBSCRIBE" => _eventing.SubscribeAsync(service, request, cancellationToken),
            "UNSUBSCRIBE" => Task.FromResult(_eventing.Unsubscribe(request)),
            _ => Task.FromResult(DlnHttpResponse.Text(405, "eventing endpoint accepts SUBSCRIBE and UNSUBSCRIBE")),
        };

    private static DlnHttpResponse Get(DlnHttpRequest request, string body)
        => request.Method is "GET" or "HEAD" ? DlnHttpResponse.Xml(body) : DlnHttpResponse.Text(405, "GET required");

    private static DlnHttpResponse FaultResponse(UpnpException exception)
        => new(500, "text/xml; charset=\"utf-8\"", SoapXml.BuildFault(exception.Code, exception.Description));

    internal ITransportRenderer Renderer => _renderer;
}
