using DlnRenderer.Core.Models;
using DlnRenderer.Dlna.AvTransport;
using DlnRenderer.Dlna.Http;

namespace DlnRenderer.Dlna.Gena;

/// <summary>
/// Owns GENA eventing for every service of this device.
/// </summary>
/// <remarks>
/// Responsibilities kept together on purpose: the registry decides who is listening, the notifier
/// delivers, and this type is the only place that knows how a service's state becomes an event body.
/// Splitting those apart would create two writers of "what the subscriber was last told".
/// <para>
/// Position is deliberately not streamed. The AV specifications expect <c>RelativeTimePosition</c> to
/// be polled via <c>GetPositionInfo</c>; sending it on every 250 ms player tick would mean four
/// HTTP requests per second per subscriber for a value the control point already polls. Notifications
/// are therefore emitted on state changes only (play, pause, stop, media change, end, error, volume,
/// mute), which is what clients actually react to.
/// </para>
/// </remarks>
public sealed class GenaEventing : IAsyncDisposable
{
    private readonly GenaOptions _options;
    private readonly GenaSubscriptionRegistry _registry;
    private readonly GenaNotifier _notifier;
    private readonly ITransportRenderer _renderer;
    private readonly Action<string> _log;
    private readonly Timer _expiryTimer;

    public GenaEventing(GenaOptions options, ITransportRenderer renderer, Action<string> log)
    {
        _options = options;
        _renderer = renderer;
        _log = log;
        _registry = new GenaSubscriptionRegistry(options);
        _notifier = new GenaNotifier(options, _registry, log);

        // Expiry is swept here rather than on every request: a subscription that nobody renews must
        // stop receiving without needing a client to touch the server again.
        // The period is derived from the shortest timeout a client can be granted and never exceeds
        // it, so an expired subscription is removed within one timeout of its deadline. The floor
        // protects against a misconfigured zero; it is lower than any sane MinTimeout (30 s here).
        var sweepMs = (int)Math.Clamp(options.MinTimeout.TotalMilliseconds / 2, 250, 30_000);
        _expiryTimer = new Timer(_ => Sweep(), null, sweepMs, sweepMs);
    }

    public GenaSubscriptionRegistry Registry => _registry;

    /// <summary>Handles SUBSCRIBE on a service's event endpoint.</summary>
    public async Task<DlnHttpResponse> SubscribeAsync(
        GenaServiceKind service,
        DlnHttpRequest request,
        CancellationToken cancellationToken)
    {
        var parsed = GenaRequestParser.ParseSubscribe(request, _options, out var subscribe);
        if (parsed.StatusCode != 200 || subscribe is null)
        {
            _log($"subscribe rejected from {request.RemoteAddress ?? "?"}: {parsed.Message}");
            return Fail(parsed);
        }

        if (subscribe.IsRenewal)
        {
            var renewal = _registry.TryRenew(subscribe.Sid!, subscribe.Timeout);
            if (renewal.StatusCode != 200)
            {
                _log($"renew rejected for {subscribe.Sid}: {renewal.Message}");
                return Fail(renewal);
            }

            _log($"subscription renewed {subscribe.Sid} ({GenaRequestParser.FormatTimeout(subscribe.Timeout)})");
            return new DlnHttpResponse(200, "text/plain; charset=\"utf-8\"", string.Empty, renewal.Headers);
        }

        var created = _registry.TryAdd(
            service,
            subscribe.Callback!,
            subscribe.Timeout,
            request.RemoteAddress ?? string.Empty,
            out var subscription);

        if (created.StatusCode != 200 || subscription is null)
        {
            _log($"subscribe refused from {request.RemoteAddress ?? "?"}: {created.Message}");
            return Fail(created);
        }

        _log($"subscription created {subscription.Sid} for {service} -> {subscription.Callback.Uri}");

        // The initial event carries every evented variable so the client never has to poll to learn
        // the current state. It is built now but delivered only after the 200 OK below has been
        // flushed: a control point that has not yet registered the SID answers 412 and, in practice,
        // throws the subscription away. A failed first delivery is not fatal either way - the
        // subscription stays until its own failure budget runs out.
        string body;
        try
        {
            body = await BuildInitialBodyAsync(service, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"initial event for {subscription.Sid} could not be built: {ex.Message}");
            body = GenaEventBuilder.BuildPropertySet([]);
        }

        var accepted = new DlnHttpResponse(200, "text/plain; charset=\"utf-8\"", string.Empty, created.Headers)
        {
            AfterSend = () => _notifier.SendInitial(subscription, body),
        };

        return accepted;
    }

    /// <summary>Handles UNSUBSCRIBE on a service's event endpoint.</summary>
    public DlnHttpResponse Unsubscribe(DlnHttpRequest request)
    {
        var parsed = GenaRequestParser.ParseUnsubscribe(request, out var sid);
        if (parsed.StatusCode != 200 || sid is null)
        {
            _log($"unsubscribe rejected from {request.RemoteAddress ?? "?"}: {parsed.Message}");
            return Fail(parsed);
        }

        // Removing an unknown SID is reported as a precondition failure: a client that unsubscribes
        // twice should learn the subscription is gone rather than assume it is still active.
        if (!_registry.TryRemove(sid))
        {
            return Fail(GenaResult.PreconditionFailed("unknown SID"));
        }

        _log($"subscription removed {sid}");
        _notifier.Forget(sid);
        return new DlnHttpResponse(200, "text/plain; charset=\"utf-8\"", string.Empty);
    }

    /// <summary>Publishes the current AVTransport state to its subscribers.</summary>
    public void PublishAvTransport()
        => _notifier.Publish(GenaServiceKind.AvTransport, () => BuildAvTransportBody());

    /// <summary>Publishes the current RenderingControl state (volume and mute) to its subscribers.</summary>
    public void PublishRenderingControl()
        => _notifier.Publish(GenaServiceKind.RenderingControl, () => BuildRenderingControlBody());

    /// <summary>Builds the AVTransport event body from the player's current state.</summary>
    /// <remarks>
    /// Synchronous by design: the notifier calls it once per publish, and it reads only cached
    /// protocol state plus <see cref="ITransportRenderer.CurrentUri"/>. Fetching a player snapshot here
    /// would block the event thread on the player twice.
    /// </remarks>
    private string BuildAvTransportBody()
    {
        var hasMedia = _renderer.CurrentUri is not null;
        return GenaEventBuilder.BuildAvTransportLastChange(
            _lastAvState,
            _lastAvStatus,
            TrackNumber(hasMedia),
            hasMedia ? 1 : 0,
            _lastAvDuration);
    }

    private string BuildRenderingControlBody()
        => GenaEventBuilder.BuildRenderingControlLastChange(_lastVolume, _lastMuted);

    /// <summary>
    /// Updates the cached AVTransport state used for event bodies and publishes when it changed.
    /// </summary>
    /// <remarks>
    /// Publishing only on change keeps the notification count proportional to what happened, not to
    /// how often the player reports progress.
    /// </remarks>
    public void UpdateAvTransport(PlaybackState state, string? error, TimeSpan? duration)
    {
        var mapped = AvTransportService.MapState(state);
        var status = string.IsNullOrEmpty(error) ? "OK" : "ERROR_OCCURRED";
        var length = AvTransportService.FormatDuration(duration);

        bool changed;
        lock (_stateGate)
        {
            changed = mapped != _lastAvState || status != _lastAvStatus || length != _lastAvDuration;
            _lastAvState = mapped;
            _lastAvStatus = status;
            _lastAvDuration = length;
        }

        if (changed)
        {
            PublishAvTransport();
        }
    }

    /// <summary>Updates the cached volume/mute and publishes when either changed.</summary>
    public void UpdateRenderingControl(int volume, bool muted)
    {
        bool changed;
        lock (_stateGate)
        {
            changed = volume != _lastVolume || muted != _lastMuted;
            _lastVolume = Math.Clamp(volume, 0, 100);
            _lastMuted = muted;
        }

        if (changed)
        {
            PublishRenderingControl();
        }
    }

    /// <summary>Caches the media URI change so the next AVTransport event carries the new track.</summary>
    public void NoteMediaChanged()
    {
        // The URI is part of every AVTransport LastChange; a media switch is a state change even when
        // the transport state happens to stay the same (A -> B while playing).
        PublishAvTransport();
    }

    private async Task<string> BuildInitialBodyAsync(GenaServiceKind service, CancellationToken cancellationToken)
    {
        switch (service)
        {
            case GenaServiceKind.AvTransport:
                {
                    var snapshot = await _renderer.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                    var hasMedia = _renderer.CurrentUri is not null;
                    UpdateAvTransport(snapshot.State, snapshot.Error, snapshot.Duration);
                    return GenaEventBuilder.BuildAvTransportLastChange(
                        AvTransportService.MapState(snapshot.State),
                        string.IsNullOrEmpty(snapshot.Error) ? "OK" : "ERROR_OCCURRED",
                        TrackNumber(hasMedia),
                        hasMedia ? 1 : 0,
                        AvTransportService.FormatDuration(snapshot.Duration));
                }

            case GenaServiceKind.RenderingControl:
                {
                    var volume = await _renderer.GetVolumeAsync(cancellationToken).ConfigureAwait(false);
                    var muted = await _renderer.GetMuteAsync(cancellationToken).ConfigureAwait(false);
                    UpdateRenderingControl(volume, muted);
                    return GenaEventBuilder.BuildRenderingControlLastChange(volume, muted);
                }

            default:
                // ConnectionManager evented variables, sent as direct properties rather than through
                // LastChange. A client subscribing here expects the sink protocol list in the initial
                // event; sending an empty property set leaves it believing the renderer accepts no
                // formats at all.
                return GenaEventBuilder.BuildPropertySet(
                [
                    new("A_ARG_TYPE_Direction", "Input"),
                    new("SinkProtocolInfo", ConnectionManagerService.SinkProtocolInfo),
                    new("CurrentConnectionIDs", "0"),
                ]);
        }
    }

    /// <summary>Track number as advertised in events: 1 while something is loaded, 0 when empty.</summary>
    private static int TrackNumber(bool hasMedia) => hasMedia ? 1 : 0;

    private static DlnHttpResponse Fail(GenaResult result)
        => new(result.StatusCode, "text/plain; charset=\"utf-8\"", result.Message ?? string.Empty);

    private void Sweep()
    {
        try
        {
            var removed = _registry.Prune();
            if (removed > 0)
            {
                _log($"{removed} expired subscription(s) removed");
            }
        }
        catch (ObjectDisposedException)
        {
            // Shutdown won the race; nothing to do.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _expiryTimer.DisposeAsync().ConfigureAwait(false);
        _registry.Clear();
        await _notifier.DisposeAsync().ConfigureAwait(false);
    }

    private readonly object _stateGate = new();
    private string _lastAvState = "STOPPED";
    private string _lastAvStatus = "OK";
    private string _lastAvDuration = "00:00:00";
    private int _lastVolume = 100;
    private bool _lastMuted;
}
