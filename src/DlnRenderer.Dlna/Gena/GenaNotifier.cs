using System.Diagnostics;
using System.Net;
using System.Text;

namespace DlnRenderer.Dlna.Gena;

/// <summary>
/// Delivers <c>NOTIFY</c> requests to subscriber callbacks.
/// </summary>
/// <remarks>
/// Notification must never influence playback. A control point that has gone away, a firewall that
/// silently drops the SYN, or a subscriber that accepts connections and never answers must all cost
/// at most one bounded, isolated attempt:
/// <list type="bullet">
/// <item>a concurrency gate caps total in-flight deliveries, so a hundred subscriptions cannot spawn
/// a hundred sockets;</item>
/// <item>connect and total request timeouts are separate, because "unreachable" and "connected but
/// silent" fail differently and both must end;</item>
/// <item>each subscription counts its own consecutive failures, so a dead client is dropped without
/// affecting anybody else;</item>
/// <item>deliveries are fire-and-forget from the caller's point of view - nothing awaits them on the
/// playback or SOAP path.</item>
/// </list>
/// </remarks>
public sealed class GenaNotifier : IAsyncDisposable
{
    private readonly GenaOptions _options;
    private readonly GenaSubscriptionRegistry _registry;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate;
    private readonly HttpClient _client;
    private readonly CancellationTokenSource _cts = new();

    public GenaNotifier(GenaOptions options, GenaSubscriptionRegistry registry, Action<string> log)
    {
        _options = options;
        _registry = registry;
        _log = log;
        _gate = new SemaphoreSlim(options.MaxConcurrentNotifications, options.MaxConcurrentNotifications);

        var handler = new SocketsHttpHandler
        {
            // Redirects are refused rather than followed: a callback that points at a public URL and
            // then bounces to an internal one would defeat the address policy.
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = options.NotifyConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),

            // No proxy: a system proxy would route LAN callbacks through a third party.
            UseProxy = false,
        };

        _client = new HttpClient(handler)
        {
            Timeout = options.NotifyRequestTimeout,
            MaxResponseContentBufferSize = options.MaxResponseBytes,
        };
    }

    /// <summary>
    /// Sends one notification to every subscriber of <paramref name="service"/>, without waiting.
    /// </summary>
    /// <remarks>
    /// Returns immediately; delivery happens on the thread pool. Exceptions cannot escape, so a
    /// failing callback can never break the play/pause path that triggered the event.
    /// </remarks>
    public void Publish(GenaServiceKind service, Func<string> bodyFactory)
    {
        IReadOnlyList<GenaSubscription> subscribers;
        try
        {
            subscribers = _registry.ForService(service);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (subscribers.Count == 0)
        {
            return;
        }

        // The body is built once: it is identical for every subscriber of the same service.
        string body;
        try
        {
            body = bodyFactory();
        }
        catch (Exception ex)
        {
            _log($"event body could not be built for {service}: {ex.Message}");
            return;
        }

        foreach (var subscription in subscribers)
        {
            _ = DeliverAsync(subscription, body);
        }
    }

    /// <summary>
    /// Sends the initial event required right after a subscription is accepted.
    /// </summary>
    /// <remarks>
    /// GENA requires the initial event to use SEQ 0 and to contain every evented variable, because it
    /// is how a subscriber learns the current state without polling. It is awaited by the caller so
    /// the 200 OK is only sent once the first notification has been attempted, matching what control
    /// points expect when they subscribe and immediately read their inbox.
    /// </remarks>
    public async Task SendInitialAsync(GenaSubscription subscription, string body, CancellationToken cancellationToken)
    {
        await DeliverAsync(subscription, body, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeliverAsync(GenaSubscription subscription, string body, CancellationToken cancellationToken = default)
    {
        var sequence = subscription.NextSequence();
        var acquired = false;
        var elapsed = Stopwatch.StartNew();

        try
        {
            // Bounded wait for a slot: with all slots busy the event is skipped rather than queued,
            // so a slow callback cannot build an unbounded backlog of stale states.
            // A saturated gate means this subscriber is not being served. That is a failure for its
            // own budget rather than a silent skip: otherwise a callback that stalls every delivery
            // would keep its SID forever and quietly starve itself.
            acquired = await _gate.WaitAsync(_options.NotifyConnectTimeout, cancellationToken).ConfigureAwait(false);
            if (!acquired)
            {
                RecordFailure(subscription, "too many notifications in flight");
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            linked.CancelAfter(_options.NotifyRequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Parse("NOTIFY"), subscription.Callback.Uri);
            request.Content = new StringContent(body, Encoding.UTF8, "text/xml");
            request.Content.Headers.ContentType!.CharSet = "\"utf-8\"";
            request.Headers.TryAddWithoutValidation("NT", "upnp:event");
            request.Headers.TryAddWithoutValidation("NTS", "upnp:propchange");
            request.Headers.TryAddWithoutValidation("SID", subscription.Sid);
            request.Headers.TryAddWithoutValidation("SEQ", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));

            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                RecordFailure(subscription, $"HTTP {(int)response.StatusCode}");
                return;
            }

            subscription.ResetFailures();

            // A successful exchange is logged as well as a failed one: a control-point report has to be
            // able to show that a real client's SUBSCRIBE was followed by a NOTIFY it answered with
            // 200, and the absence of a failure line is not the same evidence.
            _log($"notify {subscription.Sid} seq {sequence} -> {subscription.Callback.Uri} "
                + $"{(int)response.StatusCode} in {elapsed.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The service is shutting down; not a subscriber fault.
        }
        catch (OperationCanceledException)
        {
            RecordFailure(subscription, "timed out");
        }
        catch (HttpRequestException ex)
        {
            RecordFailure(subscription, ex.Message);
        }
        catch (Exception ex)
        {
            RecordFailure(subscription, ex.Message);
        }
        finally
        {
            if (acquired)
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// Counts a delivery failure and drops the subscription once it stops being a plausible client.
    /// </summary>
    /// <remarks>
    /// Dropping rather than retrying is deliberate: GENA has no retry semantics, and a control point
    /// that missed events will resubscribe (and receive the full initial state) when it reconnects.
    /// Keeping a dead subscription alive would only burn a notification attempt per state change.
    /// </remarks>
    private void RecordFailure(GenaSubscription subscription, string reason)
    {
        var failures = subscription.RecordFailure(_options.MaxConsecutiveFailures);
        if (failures >= _options.MaxConsecutiveFailures)
        {
            _registry.TryRemove(subscription.Sid);
            _log($"subscription {subscription.Sid} dropped after {failures} failed notifications ({reason})");
            return;
        }

        _log($"notify failed for {subscription.Sid} ({reason}); {failures}/{_options.MaxConsecutiveFailures}");
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        // Let in-flight deliveries unwind so no socket outlives the service, but never hang on them.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && _gate.CurrentCount < _options.MaxConcurrentNotifications)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        _client.Dispose();
        _gate.Dispose();
        _cts.Dispose();
    }
}
