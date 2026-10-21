using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

// Shared by the DLNA and Service test projects (the latter links this file).
namespace DlnRenderer.Tests.Support;

/// <summary>
/// A real HTTP endpoint that records the NOTIFY requests a renderer delivers to it.
/// </summary>
/// <remarks>
/// Tests use a real socket rather than a mock transport on purpose: the things that break in
/// production - connect timeouts, refused ports, non-success status codes, requests without a body -
/// only exist at the HTTP layer, and a mock would prove nothing about them.
/// </remarks>
public sealed class CallbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<ReceivedNotify> _received = new();
    private readonly Task _acceptLoop;

    private CallbackServer(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>HTTP status the callback answers with; change it to simulate a failing subscriber.</summary>
    public int ResponseStatus { get; set; } = 200;

    /// <summary>When set, the callback accepts the connection and then never answers.</summary>
    public bool HangRequests { get; set; }

    public int Port { get; }

    public Uri CallbackUri => new($"http://127.0.0.1:{Port}/notify");

    /// <summary>The CALLBACK header value a control point would send.</summary>
    public string CallbackHeader => $"<{CallbackUri}>";

    public IReadOnlyList<ReceivedNotify> Received => _received.ToList();

    public int Count => _received.Count;

    public static CallbackServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new CallbackServer(listener);
    }

    /// <summary>Waits for at least <paramref name="count"/> notifications, or throws on timeout.</summary>
    public async Task<IReadOnlyList<ReceivedNotify>> WaitForAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_received.Count >= count)
            {
                return _received.ToList();
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException($"expected {count} notification(s), received {_received.Count}");
    }

    /// <summary>Waits until at least one notification satisfies <paramref name="predicate"/>.</summary>
    public async Task<ReceivedNotify> WaitForAsync(Func<ReceivedNotify, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var notify in _received)
            {
                if (predicate(notify))
                {
                    return notify;
                }
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"no notification matched; received {_received.Count}: {string.Join(" | ", _received.Select(n => n.Body))}");
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

            _ = HandleAsync(client, cancellationToken);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                _received.Enqueue(request);

                if (HangRequests)
                {
                    // Accept and stay silent: the renderer must time out rather than wait forever.
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    return;
                }

                var body = Encoding.ASCII.GetBytes("ok");
                var header =
                    $"HTTP/1.1 {ResponseStatus} {(ResponseStatus == 200 ? "OK" : "Simulated")}\r\n"
                    + $"Content-Length: {body.Length}\r\n"
                    + "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A test endpoint is never allowed to fail the test run through its own socket errors.
            }
        }
    }

    private static async Task<ReceivedNotify?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var filled = 0;
        var headerEnd = -1;

        while (headerEnd < 0 && filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            filled += read;
            for (var i = 0; i < filled - 3; i++)
            {
                if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n'
                    && buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
                {
                    headerEnd = i;
                    break;
                }
            }
        }

        if (headerEnd < 0)
        {
            return null;
        }

        var lines = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        var method = lines[0].Split(' ')[0];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        var length = headers.TryGetValue("Content-Length", out var value) && int.TryParse(value, out var parsed) ? parsed : 0;
        var bodyStart = headerEnd + 4;
        var body = new byte[length];
        var alreadyRead = Math.Min(filled - bodyStart, length);
        if (alreadyRead > 0)
        {
            Array.Copy(buffer, bodyStart, body, 0, alreadyRead);
        }

        var offset = alreadyRead;
        while (offset < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, length - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return new ReceivedNotify(
            method,
            headers,
            Encoding.UTF8.GetString(body, 0, offset));
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutdown races are expected and irrelevant to the assertion.
        }

        _cts.Dispose();
    }
}

/// <summary>One NOTIFY request as the callback received it.</summary>
public sealed record ReceivedNotify(string Method, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string? Sid => Headers.GetValueOrDefault("SID");

    public string? Sequence => Headers.GetValueOrDefault("SEQ");

    public string? Nt => Headers.GetValueOrDefault("NT");

    public string? Nts => Headers.GetValueOrDefault("NTS");
}
