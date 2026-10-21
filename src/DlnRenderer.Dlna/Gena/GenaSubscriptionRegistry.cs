using System.Collections.Concurrent;
using System.Net;

namespace DlnRenderer.Dlna.Gena;

/// <summary>
/// Thread-safe registry of live GENA subscriptions.
/// </summary>
/// <remarks>
/// Two limits matter here. The total count bounds memory; the per-address count bounds what one
/// misbehaving control point can do to the others, because a client that leaks subscriptions would
/// otherwise fill the table and lock everybody else out.
/// <para>
/// The per-address counter is a plain dictionary guarded by the same lock as the subscription table,
/// incremented when a subscription is created and decremented when exactly that subscription is
/// removed. Earlier attempts to recount from the callback host were wrong in both directions: a
/// control point may hold several SIDs from one host, and the callback host is not necessarily the
/// address that subscribed.
/// </para>
/// </remarks>
public sealed class GenaSubscriptionRegistry(GenaOptions options)
{
    private const string CountKey = "__count__";

    private readonly Dictionary<string, GenaSubscription> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _perAddress = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Number of live (non-expired) subscriptions.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _subscriptions.Count;
            }
        }
    }

    /// <summary>Creates a subscription, or explains why it could not be created.</summary>
    public GenaResult TryAdd(
        GenaServiceKind service,
        GenaCallback callback,
        TimeSpan timeout,
        string remoteAddress,
        out GenaSubscription? subscription)
    {
        subscription = null;
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            PruneLocked(now);

            if (_subscriptions.Count >= options.MaxSubscriptions)
            {
                return GenaResult.ServerError("too many subscriptions");
            }

            var address = string.IsNullOrEmpty(remoteAddress) ? CountKey : remoteAddress;
            var perAddress = _perAddress.GetValueOrDefault(address);
            if (perAddress >= options.MaxSubscriptionsPerAddress)
            {
                return GenaResult.ServerError("too many subscriptions from this address");
            }

            var sid = GenaRequestParser.CreateSid();
            subscription = new GenaSubscription(sid, service, callback, timeout, address);
            _subscriptions[sid] = subscription;
            _perAddress[address] = perAddress + 1;
            return GenaResult.Ok(new Dictionary<string, string>
            {
                ["SID"] = sid,
                ["TIMEOUT"] = GenaRequestParser.FormatTimeout(subscription.Timeout),
            });
        }
    }

    /// <summary>Extends an existing subscription. The SID is preserved, as the spec requires.</summary>
    public GenaResult TryRenew(string sid, TimeSpan timeout)
    {
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(sid, out var subscription))
            {
                return GenaResult.PreconditionFailed("unknown SID");
            }

            if (subscription.IsExpired(DateTimeOffset.UtcNow))
            {
                // An expired subscription is gone: the client must subscribe again rather than
                // silently reactivating a SID other clients may already have been told about.
                RemoveLocked(subscription);
                return GenaResult.PreconditionFailed("subscription expired");
            }

            subscription.Renew(timeout);
            return GenaResult.Ok(new Dictionary<string, string>
            {
                ["SID"] = subscription.Sid,
                ["TIMEOUT"] = GenaRequestParser.FormatTimeout(subscription.Timeout),
            });
        }
    }

    public bool TryGet(string sid, out GenaSubscription? subscription)
    {
        lock (_gate)
        {
            return _subscriptions.TryGetValue(sid, out subscription);
        }
    }

    /// <summary>Removes a subscription. Removing an unknown or already removed SID returns false.</summary>
    public bool TryRemove(string sid)
    {
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(sid, out var subscription))
            {
                return false;
            }

            RemoveLocked(subscription);
            return true;
        }
    }

    /// <summary>Snapshot of the live subscriptions for one service, used when fanning out a notification.</summary>
    public IReadOnlyList<GenaSubscription> ForService(GenaServiceKind service)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            return _subscriptions.Values
                .Where(s => s.Service == service && !s.IsExpired(now))
                .ToList();
        }
    }

    /// <summary>Drops expired subscriptions and returns how many were removed.</summary>
    public int Prune()
    {
        lock (_gate)
        {
            return PruneLocked(DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Removes everything; used on shutdown so no notification outlives the service.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _subscriptions.Clear();
            _perAddress.Clear();
        }
    }

    private int PruneLocked(DateTimeOffset now)
    {
        var expired = _subscriptions.Values.Where(s => s.IsExpired(now)).ToList();
        foreach (var subscription in expired)
        {
            RemoveLocked(subscription);
        }

        return expired.Count;
    }

    /// <summary>Removes one subscription and releases exactly its per-address slot.</summary>
    private void RemoveLocked(GenaSubscription subscription)
    {
        if (!_subscriptions.Remove(subscription.Sid))
        {
            return;
        }

        if (!_perAddress.TryGetValue(subscription.AddressKey, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            _perAddress.Remove(subscription.AddressKey);
        }
        else
        {
            _perAddress[subscription.AddressKey] = count - 1;
        }
    }
}
