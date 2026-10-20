using System.Net;
using DlnRenderer.Dlna.AvTransport;
using DlnRenderer.Dlna.Http;
using DlnRenderer.Dlna.Soap;
using DlnRenderer.Dlna.Ssdp;
using DlnRenderer.Dlna.Upnp;

namespace DlnRenderer.Dlna;

/// <summary>
/// Owns the network side of the renderer: the HTTP endpoint (description, SCPDs, SOAP control)
/// and the SSDP responder/announcer. Protocol handling itself lives in the services, so this type
/// only routes requests and manages lifetimes.
/// </summary>
public sealed class DlnaDeviceHost : IAsyncDisposable
{
    private readonly DlnaOptions _options;
    private readonly ITransportRenderer _renderer;
    private readonly Action<string> _log;
    private readonly AvTransportService _avTransport;
    private readonly ConnectionManagerService _connectionManager = new();

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
    }

    public bool IsRunning => _http is not null;

    public string? DescriptionUrl { get; private set; }

    public string? ControlUrl { get; private set; }

    public int HttpPort => _http?.Port ?? 0;

    public bool SsdpActive => _ssdp?.IsRunning == true;

    public IReadOnlyList<IPAddress> SsdpAddresses => _ssdp?.BoundAddresses ?? [];

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
        var description = DeviceDescriptionBuilder.Build(_options);
        var avTransportScpd = ScpdBuilder.BuildAvTransport();
        var connectionManagerScpd = ScpdBuilder.BuildConnectionManager();

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

        _description = description;
        _avTransportScpd = avTransportScpd;
        _connectionManagerScpd = connectionManagerScpd;
    }

    private string _description = string.Empty;
    private string _avTransportScpd = string.Empty;
    private string _connectionManagerScpd = string.Empty;

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
        try
        {
            return request.Path switch
            {
                DlnaPaths.Description => Get(request, _description),
                DlnaPaths.AvTransportScpd => Get(request, _avTransportScpd),
                DlnaPaths.ConnectionManagerScpd => Get(request, _connectionManagerScpd),
                DlnaPaths.AvTransportControl => await HandleSoapAsync(
                    request,
                    r => _avTransport.HandleAsync(r, cancellationToken),
                    cancellationToken).ConfigureAwait(false),
                DlnaPaths.ConnectionManagerControl => await HandleSoapAsync(
                    request,
                    r => Task.FromResult(_connectionManager.Handle(r)),
                    cancellationToken).ConfigureAwait(false),
                DlnaPaths.AvTransportEvent or DlnaPaths.ConnectionManagerEvent =>
                    DlnHttpResponse.Text(501, "GENA eventing is not implemented by this renderer."),
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
        var response = await handler(parsed).ConfigureAwait(false);
        _ = cancellationToken;
        return DlnHttpResponse.Soap(response);
    }

    private static DlnHttpResponse Get(DlnHttpRequest request, string body)
        => request.Method is "GET" or "HEAD" ? DlnHttpResponse.Xml(body) : DlnHttpResponse.Text(405, "GET required");

    private static DlnHttpResponse FaultResponse(UpnpException exception)
        => new(500, "text/xml; charset=\"utf-8\"", SoapXml.BuildFault(exception.Code, exception.Description));

    internal ITransportRenderer Renderer => _renderer;
}
