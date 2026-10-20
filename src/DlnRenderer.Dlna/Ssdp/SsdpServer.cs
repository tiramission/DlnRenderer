using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DlnRenderer.Dlna.Ssdp;

/// <summary>
/// Listens for M-SEARCH, answers with the renderer description, and announces
/// ssdp:alive / ssdp:byebye.
/// </summary>
/// <remarks>
/// Socket strategy, verified against a real LAN with several adapters (physical Ethernet, Hyper-V,
/// WSL, a VPN tunnel) and Windows' own SSDP service running:
/// <list type="bullet">
/// <item>
/// One socket bound to the wildcard address (0.0.0.0:1900) is used for both receiving and sending.
/// Binding per interface instead looks equivalent but is not: Windows keeps a separate bucket per
/// exact local address, and when another process (SSDPSRV, another renderer) already owns that
/// address, multicast datagrams are delivered to only one of the sockets in the bucket. The wildcard
/// bucket is independent, which is why renderers written this way are discovered reliably.
/// </item>
/// <item>
/// The multicast group is joined once per usable interface, so every NIC receives the search.
/// </item>
/// </list>
/// </remarks>
public sealed class SsdpServer : IAsyncDisposable
{
    /// <summary>Window in which a repeat of the same search from the same requester is ignored.</summary>
    // Must stay well below the retry interval of real control points (they re-send within the MX
    // window when an answer is lost) and only long enough to swallow the same datagram arriving on
    // several joined interfaces. 250 ms does that; 2 s silently dropped genuine retries.
    private const int DuplicateSearchWindowMs = 250;

    private readonly DlnaOptions _options;
    private readonly Func<IPAddress, string> _locationProvider;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _responseGate = new(16, 16);

    /// <summary>
    /// Recent searches keyed by requester and ST, so a repeated M-SEARCH is answered once.
    /// Repeated answers make strict control points treat the renderer as misbehaving.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _recentSearches = new();

    private readonly List<IPAddress> _joinedInterfaces = [];

    private Socket? _socket;
    private Task? _receiveLoop;
    private Task? _aliveLoop;

    public SsdpServer(DlnaOptions options, Func<IPAddress, string> locationProvider, Action<string> log)
    {
        _options = options;
        _locationProvider = locationProvider;
        _log = log;
    }

    public bool IsRunning { get; private set; }

    /// <summary>Interfaces the multicast group was successfully joined on.</summary>
    public IReadOnlyList<IPAddress> BoundAddresses => _joinedInterfaces;

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

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // Share the port so we coexist with SSDPSRV and other renderers; SO_REUSEADDR also lets
            // this process receive multicast that is delivered to the wildcard address.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, SsdpConstants.MulticastPort));

            // Multicast must not loop back to ourselves: the loopback copy would be answered again.
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, false);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new InvalidOperationException(
                $"SSDP could not bind UDP {SsdpConstants.MulticastPort}: {ex.SocketErrorCode}. " +
                "Another renderer may be running on this machine.", ex);
        }

        var failures = new List<string>();
        foreach (var address in addresses)
        {
            try
            {
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.AddMembership,
                    new MulticastOption(multicast, address));
                _joinedInterfaces.Add(address);
            }
            catch (SocketException ex)
            {
                failures.Add($"{address}: {ex.SocketErrorCode}");
            }
        }

        if (_joinedInterfaces.Count == 0)
        {
            socket.Dispose();
            throw new InvalidOperationException(
                $"SSDP could not join {SsdpConstants.MulticastAddress} on any interface ({string.Join("; ", failures)}).");
        }

        if (failures.Count > 0)
        {
            _log($"ssdp could not join multicast on: {string.Join("; ", failures)}");
        }

        _socket = socket;
        IsRunning = true;
        _log($"ssdp bound 0.0.0.0:{SsdpConstants.MulticastPort}, multicast joined on {string.Join(", ", _joinedInterfaces)}");

        SendNotifications(alive: true);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(socket, _cts.Token));
        _aliveLoop = Task.Run(AliveLoopAsync);
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsRunning)
        {
            _cts.Dispose();
            _responseGate.Dispose();
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

        var multicast = IPAddress.Parse(SsdpConstants.MulticastAddress);
        if (_socket is not null)
        {
            foreach (var address in _joinedInterfaces)
            {
                try
                {
                    _socket.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.DropMembership,
                        new MulticastOption(multicast, address));
                }
                catch (SocketException)
                {
                }
            }

            try
            {
                _socket.Close();
            }
            catch (SocketException)
            {
            }
        }

        foreach (var loop in new[] { _receiveLoop, _aliveLoop })
        {
            if (loop is null)
            {
                continue;
            }

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
            catch (SocketException ex)
            {
                // ICMP port unreachable or a transient reset must not end the listener.
                _log($"ssdp receive error: {ex.SocketErrorCode}");
                continue;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes);
            var from = result.RemoteEndPoint.ToString();

            if (!SsdpMessage.TryParseSearch(text, out var search) || search is null)
            {
                _log("ssdp datagram ignored: not a valid M-SEARCH");
                continue;
            }

            _log($"ssdp M-SEARCH received from {from} ST={search.SearchTarget} MX={search.Mx}");

            if (!SsdpMessage.Matches(search.SearchTarget, _options.Uuid))
            {
                _log($"ssdp not answered: ST={search.SearchTarget} is not something this device provides");
                continue;
            }

            var dedupeKey = $"{from}|{search.SearchTarget}";
            var now = Environment.TickCount64;
            if (_recentSearches.TryGetValue(dedupeKey, out var lastSeen) && now - lastSeen < DuplicateSearchWindowMs)
            {
                _log($"ssdp duplicate search ignored ST={search.SearchTarget} from {from}");
                continue;
            }

            _recentSearches[dedupeKey] = now;
            if (_recentSearches.Count > 256)
            {
                foreach (var entry in _recentSearches)
                {
                    if (now - entry.Value >= DuplicateSearchWindowMs)
                    {
                        _recentSearches.TryRemove(entry.Key, out _);
                    }
                }
            }

            if (!_responseGate.Wait(0))
            {
                _log("ssdp responder saturated; dropping a search");
                continue;
            }

            var remoteEndPoint = result.RemoteEndPoint;
            // The address the request arrived on selects the LOCATION host, so reply from that
            // interface rather than from whatever the default route happens to be.
            var localAddress = (result.RemoteEndPoint as IPEndPoint) is { } requester
                ? ResolveLocalAddressFor(requester.Address)
                : IPAddress.Any;

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        // Random delay inside the requester's MX window avoids response storms.
                        var delay = Random.Shared.Next(0, Math.Max(1, search.Mx * 400));
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        await RespondAsync(socket, remoteEndPoint, localAddress, search.SearchTarget, cancellationToken)
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

    private async Task RespondAsync(
        Socket socket,
        EndPoint remote,
        IPAddress localAddress,
        string searchTarget,
        CancellationToken cancellationToken)
    {
        var location = _locationProvider(localAddress);

        // Reply only for the target that was actually searched for (or every target for ssdp:all).
        var targets = SsdpMessage.TargetsForSearch(searchTarget, _options.Uuid);
        foreach (var (target, usn) in targets)
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

        _log($"ssdp answered {targets.Count} target(s) for ST={searchTarget} to {remote} LOCATION={location}");
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
        var socket = _socket;
        if (socket is null)
        {
            return;
        }

        var group = new IPEndPoint(IPAddress.Parse(SsdpConstants.MulticastAddress), SsdpConstants.MulticastPort);

        foreach (var address in _joinedInterfaces)
        {
            var location = _locationProvider(address);

            // Announce from this interface: without pinning the outgoing interface, the notification
            // follows the default route and can leave from a VPN or Hyper-V adapter.
            try
            {
                var interfaceIndex = InterfaceIndexOf(address);
                if (interfaceIndex > 0)
                {
                    // IP_MULTICAST_IF expects the interface index in network byte order.
                    socket.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.MulticastInterface,
                        SwapByteOrder(interfaceIndex));
                }
            }
            catch (SocketException ex)
            {
                _log($"ssdp multicast interface for {address} could not be set: {ex.SocketErrorCode}");
            }

            foreach (var (target, usn) in SsdpMessage.AdvertisedTargets(_options.Uuid))
            {
                var payload = SsdpMessage.BuildNotify(target, alive, usn, location, _options.ServerHeader, _options.MaxAge);
                try
                {
                    socket.SendTo(Encoding.ASCII.GetBytes(payload), group);
                }
                catch (SocketException ex)
                {
                    _log($"ssdp notify send failed on {address}: {ex.SocketErrorCode}");
                }
            }
        }

        if (alive)
        {
            _log($"ssdp alive announced on {string.Join(", ", _joinedInterfaces)}");
        }
    }

    /// <summary>
    /// Chooses the local address to advertise for a requester: the interface whose subnet contains
    /// it, so a control point is never handed a LOCATION on a different network.
    /// </summary>
    private IPAddress ResolveLocalAddressFor(IPAddress requester)
    {
        var address = NetworkInterfaces.SelectSourceAddressFor(requester, _joinedInterfaces);
        return address ?? (_joinedInterfaces.Count > 0 ? _joinedInterfaces[0] : IPAddress.Any);
    }

    /// <summary>Reverses the four bytes of an interface index (host order to network order).</summary>
    private static int SwapByteOrder(int value)
        => (value >> 24) | ((value >> 8) & 0xFF00) | ((value << 8) & 0xFF0000) | (value << 24);

    private static int InterfaceIndexOf(IPAddress address)
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.Equals(address))
                {
                    return nic.GetIPProperties().GetIPv4Properties()?.Index ?? 0;
                }
            }
        }

        return 0;
    }
}
