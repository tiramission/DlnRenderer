using System.Net;

namespace DlnRenderer.Dlna.Gena;

/// <summary>How much of a service's state a subscription asks to receive.</summary>
public enum GenaServiceKind
{
    AvTransport,
    RenderingControl,
    ConnectionManager,
}

/// <summary>
/// Validated, normalised callback target for one subscription.
/// </summary>
/// <param name="Uri">Absolute http URI the device will POST NOTIFY to.</param>
public sealed record GenaCallback(Uri Uri)
{
    public override string ToString() => Uri.ToString();
}

/// <summary>
/// One active GENA subscription.
/// </summary>
/// <remarks>
/// A subscription owns its own sequence counter: GENA requires SEQ to start at 0 for the initial
/// event and increase by one per notification, per subscriber. Two control points watching the same
/// service therefore must not share a counter, or each would see gaps and discard events.
/// </remarks>
public sealed class GenaSubscription
{
    private long _sequence;

    public GenaSubscription(string sid, GenaServiceKind service, GenaCallback callback, TimeSpan timeout, string addressKey = "")
    {
        Sid = sid;
        Service = service;
        Callback = callback;
        Timeout = timeout;
        AddressKey = addressKey;
        ExpiresAt = DateTimeOffset.UtcNow + timeout;
    }

    /// <summary>Remote address that created the subscription; key for the per-address limit.</summary>
    public string AddressKey { get; }

    /// <summary>Subscription identifier sent as <c>SID: uuid:...</c>.</summary>
    public string Sid { get; }

    public GenaServiceKind Service { get; }

    public GenaCallback Callback { get; }

    /// <summary>Timeout granted on the last SUBSCRIBE (new or renewal).</summary>
    public TimeSpan Timeout { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>
    /// SEQ for the next notification. The initial event uses 0, so the first call returns 0.
    /// </summary>
    /// <remarks>GENA numbers events per subscriber; wrap-around at 2^32 is defined by the spec.</remarks>
    public uint NextSequence() => unchecked((uint)Interlocked.Increment(ref _sequence) - 1);

    /// <summary>True once the subscription lifetime elapsed without a renewal.</summary>
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>
    /// Moves the deadline, so expiry can be exercised without waiting for wall-clock time.
    /// Used by tests and by nothing else; a production path never needs to shorten a subscription.
    /// </summary>
    internal void ExpireAt(DateTimeOffset expiresAt) => ExpiresAt = expiresAt;

    /// <summary>Consecutive failed notifications since the last success.</summary>
    private int _failures;

    /// <summary>
    /// Records a failed delivery and returns the new consecutive-failure count.
    /// </summary>
    /// <remarks>
    /// Clamped at the budget: concurrent deliveries all increment, and without a ceiling a burst would
    /// push the counter past the threshold before any of them got to remove the subscription.
    /// </remarks>
    public int RecordFailure(int budget)
    {
        var value = Interlocked.Increment(ref _failures);
        return value > budget ? budget : value;
    }

    /// <summary>Resets the failure counter after a delivery the callback accepted.</summary>
    public void ResetFailures() => Interlocked.Exchange(ref _failures, 0);

    /// <summary>Current consecutive failure count, for diagnostics.</summary>
    public int ConsecutiveFailures => Volatile.Read(ref _failures);

    /// <summary>Extends an existing subscription. The SID and SEQ are deliberately preserved.</summary>
    public void Renew(TimeSpan timeout)
    {
        Timeout = timeout;
        ExpiresAt = DateTimeOffset.UtcNow + timeout;
    }
}

/// <summary>Outcome of a SUBSCRIBE / UNSUBSCRIBE request, expressed as HTTP.</summary>
/// <param name="StatusCode">HTTP status to return.</param>
/// <param name="Headers">Response headers (SID, TIMEOUT, ...), absent on failures.</param>
/// <param name="Message">Human-readable reason for logs and error bodies.</param>
public sealed record GenaResult(int StatusCode, IReadOnlyDictionary<string, string>? Headers = null, string? Message = null)
{
    public static GenaResult Ok(IReadOnlyDictionary<string, string> headers) => new(200, headers);

    public static GenaResult BadRequest(string message) => new(400, null, message);

    /// <summary>"Missing or invalid header fields": the spec's answer for bad SUBSCRIBE headers.</summary>
    public static GenaResult PreconditionFailed(string message) => new(412, null, message);

    /// <summary>The eventing endpoint refused to create a subscription (capacity, policy).</summary>
    public static GenaResult ServerError(string message) => new(500, null, message);
}

/// <summary>Limits applied to eventing so a hostile or buggy control point cannot exhaust the service.</summary>
public sealed record GenaOptions
{
    /// <summary>Maximum concurrent subscriptions across all services.</summary>
    public int MaxSubscriptions { get; init; } = 64;

    /// <summary>Maximum subscriptions a single remote address may hold.</summary>
    public int MaxSubscriptionsPerAddress { get; init; } = 8;

    /// <summary>Timeout granted on new subscriptions.</summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Upper bound for a client-requested TIMEOUT.</summary>
    public TimeSpan MaxTimeout { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Lower bound for a client-requested TIMEOUT; the spec treats very short ones as useless.</summary>
    public TimeSpan MinTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for the callback to accept the NOTIFY.</summary>
    public TimeSpan NotifyConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Total time allowed for one NOTIFY exchange, including the response body.</summary>
    public TimeSpan NotifyRequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum bytes read from a callback response; anything larger is treated as a failure.</summary>
    public int MaxResponseBytes { get; init; } = 8 * 1024;

    /// <summary>Maximum NOTIFY exchanges in flight at once, across all subscriptions.</summary>
    public int MaxConcurrentNotifications { get; init; } = 8;

    /// <summary>
    /// Consecutive failed notifications before a subscription is dropped. Without a bound a dead
    /// callback would be retried for the whole subscription lifetime on every state change.
    /// </summary>
    public int MaxConsecutiveFailures { get; init; } = 5;

    /// <summary>
    /// Allow callbacks to loopback (127.0.0.0/8, ::1). Off by default: it would turn the renderer
    /// into a probe for every service listening on the host.
    /// </summary>
    public bool AllowLoopbackCallbacks { get; init; }

    /// <summary>
    /// Allow callbacks to private/link-local ranges. On by default, because real control points are
    /// phones and tablets on the same LAN - restricting callbacks to the responder's own subnet
    /// breaks clients that reach the renderer through a second interface or a VPN.
    /// </summary>
    public bool AllowPrivateNetworkCallbacks { get; init; } = true;
}
