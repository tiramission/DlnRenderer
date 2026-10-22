using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DlnRenderer.Dlna.Http;

public sealed record DlnHttpRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    string? RemoteAddress = null);

public sealed record DlnHttpResponse(int StatusCode, string ContentType, string Body, IReadOnlyDictionary<string, string>? Headers = null)
{
    /// <summary>
    /// Work to run after the response has been flushed to the client.
    /// </summary>
    /// <remarks>
    /// GENA needs it: the initial event of a subscription must not reach the control point before
    /// the <c>200 OK</c> that carries the SID, or the client answers 412 for an unknown subscription.
    /// </remarks>
    public Action? AfterSend { get; init; }

    public static DlnHttpResponse Xml(string body) => new(200, "text/xml; charset=\"utf-8\"", body);

    public static DlnHttpResponse Soap(string body) =>
        new(200, "text/xml; charset=\"utf-8\"", body, new Dictionary<string, string> { ["EXT"] = string.Empty });

    public static DlnHttpResponse Text(int statusCode, string message) =>
        new(statusCode, "text/plain; charset=\"utf-8\"", message);
}

/// <summary>
/// Deliberately small HTTP/1.1 server: one request per connection, bounded headers and body,
/// no keep-alive. Control points only need GET descriptions and POST SOAP.
/// </summary>
public sealed class SimpleHttpServer : IAsyncDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 64 * 1024;

    private readonly Func<DlnHttpRequest, CancellationToken, Task<DlnHttpResponse>> _handler;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public SimpleHttpServer(IPAddress address, int port, Func<DlnHttpRequest, CancellationToken, Task<DlnHttpResponse>> handler)
    {
        _handler = handler;
        _listener = new TcpListener(address, port);
        Address = address;
    }

    public int Port { get; private set; }

    public IPAddress Address { get; }

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                // Pair the request with its sender so diagnostics can tell "never fetched" apart
                // from "fetched and rejected".
                if (client.Client.RemoteEndPoint is IPEndPoint remote)
                {
                    request = request with { RemoteAddress = remote.Address.ToString() };
                }

                var response = await _handler(request, cancellationToken).ConfigureAwait(false);
                await WriteResponseAsync(stream, response, cancellationToken).ConfigureAwait(false);
                RunAfterSend(response);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                // A broken control point connection must never take the server down.
            }
        }
    }

    private static async Task<DlnHttpRequest?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHeaderBytes];
        var filled = 0;
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            if (filled >= buffer.Length)
            {
                return null;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            filled += read;
            headerEnd = FindHeaderEnd(buffer, filled);
        }

        var headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var lines = headerText.Split(["\r\n", "\n"], StringSplitOptions.None);
        if (lines.Length == 0)
        {
            return null;
        }

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var separator = lines[i].IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            headers[lines[i][..separator].Trim()] = lines[i][(separator + 1)..].Trim();
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var lengthText))
        {
            if (!int.TryParse(lengthText, out contentLength) || contentLength < 0 || contentLength > MaxBodyBytes)
            {
                return null;
            }
        }

        var bodyStart = headerEnd + HeaderDelimiterLength(buffer, headerEnd);
        var alreadyRead = filled - bodyStart;
        var body = new byte[contentLength];
        if (alreadyRead > 0)
        {
            Array.Copy(buffer, bodyStart, body, 0, Math.Min(alreadyRead, contentLength));
        }

        var remaining = contentLength - Math.Min(alreadyRead, contentLength);
        var offset = Math.Min(alreadyRead, contentLength);
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, remaining), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            offset += read;
            remaining -= read;
        }

        var path = requestLine[1];
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
        {
            path = path[..queryIndex];
        }

        return new DlnHttpRequest(
            requestLine[0].ToUpperInvariant(),
            path,
            headers,
            Encoding.UTF8.GetString(body));
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 0; i < length - 3; i++)
        {
            if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n'
                && buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static int HeaderDelimiterLength(byte[] buffer, int headerEnd)
        => buffer[headerEnd] == (byte)'\r' ? 4 : 2;

    private static async Task WriteResponseAsync(Stream stream, DlnHttpResponse response, CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(response.Body);
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(ReasonPhrase(response.StatusCode)).Append("\r\n");
        builder.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        builder.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        builder.Append("Connection: close\r\n");
        builder.Append("Server: Windows/10 UPnP/1.0 DlnRenderer/0.1\r\n");
        // RFC 7231 requires an origin server to send Date; strict control points reject replies without it.
        builder.Append("Date: ").Append(DateTime.UtcNow.ToString("r", System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");

        if (response.Headers is not null)
        {
            foreach (var (name, value) in response.Headers)
            {
                builder.Append(name).Append(':');
                if (value.Length > 0)
                {
                    builder.Append(' ').Append(value);
                }

                builder.Append("\r\n");
            }
        }

        builder.Append("\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the response's after-send hook. Failures are swallowed: the response is already on the
    /// wire, so a fault here can only be logged by whoever wrote the hook.
    /// </summary>
    private static void RunAfterSend(DlnHttpResponse response)
    {
        var afterSend = response.AfterSend;
        if (afterSend is null)
        {
            return;
        }

        try
        {
            afterSend();
        }
        catch (Exception)
        {
            // Deliberately ignored - see remarks.
        }
    }

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        _ => "OK",
    };
}
