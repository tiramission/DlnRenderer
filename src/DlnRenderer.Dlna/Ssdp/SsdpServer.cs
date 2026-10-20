using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DlnRenderer.Dlna.Ssdp;

/// <summary>
/// Listens for M-SEARCH on every eligible interface, answers with the renderer description,
/// and announces ssdp:alive / ssdp:byebye.
/// </summary>
public sealed class SsdpServer : IAsyncDisposable
{
    private readonly DlnaOptions _options;
    private readonly Func<IPAddress, string> _locationProvider;
    private readonly Action<string> _log;
    private readonly List<Socket> _sockets = [];
    private readonly List<Task> _receiveLoops = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _responseGate = new(16, 16);
    private Task? _aliveLoop;

    public SsdpServer(DlnaOptions options, Func<IPAddress, string> locationProvider, Action<string> log)
    {
        _options = options;
        _locationProvider = locationProvider;
        _log = log;
    }

    public bool IsRunning { get; private set; }

    public IReadOnlyList<IPAddress> BoundAddresses { get; private set; } = [];

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        var addresses = NetworkInterfaces.GetLanAddresses(_options.ListenAddress);
        if (addresses.Count == 0)
        {
            throw new InvalidOperationException("No usable IPv4 interface was found for SSDP.");
        }

        var multicast = IPAddress.Parse(SsdpConstants.MulticastAddress);
        var bound = new List<IPAddress>();
        var failures = new List<string>();

        foreach (var address in addresses)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(address, SsdpConstants.MulticastPort));
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.AddMembership,
                    new MulticastOption(multicast, address));

                _sockets.Add(socket);
                bound.Add(address);
                _receiveLoops.Add(Task.Run(() => ReceiveLoopAsync(socket, _cts.Token)));
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                failures.Add($"{address}: {ex.SocketErrorCode}");
            }
        }

        if (_sockets.Count == 0)
        {
            throw new InvalidOperationException(
                $"SSDP could not bind port 1900 ({string.Join("; ", failures)}). Another renderer may be running.");
        }

        BoundAddresses = bound;
        IsRunning = true;
        _log($"ssdp listening on {string.Join(", ", bound)} (UDP 1900)");

        SendNotifications(alive: true);
        _aliveLoop = Task.Run(AliveLoopAsync);
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsRunning)
        {
            _cts.Dispose();
            return;
        }

        IsRunning = false;

        try
        {
            SendNotifications(alive: false);
        }
        catch (Exception ex)
        {
            _log($"ssdp byebye failed: {ex.Message}");
        }

        await _cts.CancelAsync().ConfigureAwait(false);

        foreach (var socket in _sockets)
        {
            try
            {
                socket.Close();
            }
            catch (SocketException)
            {
            }
        }

        foreach (var loop in _receiveLoops)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (_aliveLoop is not null)
        {
            try
            {
                await _aliveLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _responseGate.Dispose();
        _cts.Dispose();
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, cancellationToken)
                    .ConfigureAwait(false);
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

            var text = Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes);
            if (!SsdpMessage.TryParseSearch(text, out var search) || search is null)
            {
                continue;
            }

            if (!SsdpMessage.Matches(search.SearchTarget, _options.Uuid))
            {
                continue;
            }

            if (!_responseGate.Wait(0))
            {
                _log("ssdp responder saturated; dropping a search");
                continue;
            }

            var remoteEndPoint = result.RemoteEndPoint;
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        // Random delay inside the requester's MX window avoids response storms.
                        var delay = Random.Shared.Next(0, Math.Max(1, search.Mx * 400));
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        await RespondAsync(socket, remoteEndPoint, search.SearchTarget, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (SocketException ex)
                    {
                        _log($"ssdp response failed: {ex.SocketErrorCode}");
                    }
                    finally
                    {
                        _responseGate.Release();
                    }
                },
                CancellationToken.None);
        }
    }

    private async Task RespondAsync(Socket socket, EndPoint remote, string searchTarget, CancellationToken cancellationToken)
    {
        // Advertise the address the request actually arrived on: with several interfaces the
        // requester can only reach the one it used.
        var localAddress = ((IPEndPoint)socket.LocalEndPoint!).Address;
        var location = _locationProvider(localAddress);
        foreach (var (target, usn) in SsdpMessage.AdvertisedTargets(_options.Uuid))
        {
            var response = SsdpMessage.BuildSearchResponse(
                target,
                usn,
                location,
                _options.ServerHeader,
                _options.MaxAge);

            var bytes = Encoding.ASCII.GetBytes(response);
            await socket.SendToAsync(bytes, SocketFlags.None, remote, cancellationToken).ConfigureAwait(false);
        }

        _log($"ssdp answered {searchTarget} to {remote}");
    }

    private async Task AliveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(_options.AliveInterval, _cts.Token).ConfigureAwait(false);
                SendNotifications(alive: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SendNotifications(bool alive)
    {
        var multicast = new IPEndPoint(IPAddress.Parse(SsdpConstants.MulticastAddress), SsdpConstants.MulticastPort);

        foreach (var socket in _sockets)
        {
            var localAddress = ((IPEndPoint)socket.LocalEndPoint!).Address;
            var location = _locationProvider(localAddress);

            foreach (var (target, usn) in SsdpMessage.AdvertisedTargets(_options.Uuid))
            {
                var payload = SsdpMessage.BuildNotify(target, alive, usn, location, _options.ServerHeader, _options.MaxAge);
                socket.SendTo(Encoding.ASCII.GetBytes(payload), multicast);
            }
        }

        if (alive)
        {
            _log("ssdp alive announced");
        }
    }
}
