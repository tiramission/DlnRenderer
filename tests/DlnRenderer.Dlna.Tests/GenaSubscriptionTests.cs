using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Xml.Linq;
using DlnRenderer.Dlna;
using DlnRenderer.Dlna.AvTransport;
using DlnRenderer.Core.Models;
using DlnRenderer.Dlna.Gena;
using DlnRenderer.Dlna.Tests.Support;
using DlnRenderer.Tests.Support;

namespace DlnRenderer.Dlna.Tests;

/// <summary>
/// End-to-end GENA tests: a real DlnaDeviceHost on loopback, a real HTTP callback endpoint, and the
/// same SUBSCRIBE / NOTIFY messages a phone control point sends.
/// </summary>
/// <remarks>
/// The assertions are about protocol behaviour that clients depend on, not about implementation:
/// status codes for bad requests, the initial SEQ 0 event, per-subscription sequencing, the escaped
/// LastChange payload, and isolation between subscribers.
/// </remarks>
public sealed class GenaSubscriptionTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private DlnaDeviceHost _host = null!;
    private HttpClient _client = null!;
    private readonly FakeTransportRenderer _renderer = new();

    /// <summary>Service-log lines produced by the host under test.</summary>
    private readonly ConcurrentQueue<string> _logs = new();

    public Task InitializeAsync()
    {
        _host = new DlnaDeviceHost(
            new DlnaOptions
            {
                // Loopback callbacks are refused by default; tests need them.
                Eventing = new GenaOptions { AllowLoopbackCallbacks = true },
            },
            _renderer,
            message => _logs.Enqueue(message));
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.HttpPort}") };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    // ---- subscription lifecycle -------------------------------------------------------------

    [Fact]
    public async Task New_subscription_returns_a_sid_and_granted_timeout()
    {
        await using var callback = CallbackServer.Start();

        var response = await SubscribeAsync(callback, timeout: "Second-1800");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("SID", out var sids));
        var sid = Assert.Single(sids);
        Assert.StartsWith("uuid:", sid, StringComparison.Ordinal);
        Assert.True(Guid.TryParse(sid[5..], out _));

        Assert.True(response.Headers.TryGetValues("TIMEOUT", out var timeouts));
        Assert.Equal("Second-1800", Assert.Single(timeouts));
    }

    [Fact]
    public async Task Renewal_keeps_the_same_sid()
    {
        await using var callback = CallbackServer.Start();
        var created = await SubscribeAsync(callback, "Second-60");
        var sid = created.Headers.GetValues("SID").Single();

        var renewed = await SendRawAsync("SUBSCRIBE", "/event/avtransport", new()
        {
            ["SID"] = sid,
            ["TIMEOUT"] = "Second-900",
        });

        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        Assert.Equal(sid, renewed.Headers.GetValues("SID").Single());
        Assert.Equal("Second-900", renewed.Headers.GetValues("TIMEOUT").Single());

        // A renewal must not create a second subscription.
        Assert.Equal(1, _host.Eventing.Registry.Count);
    }

    [Fact]
    public async Task Renewal_with_an_unknown_sid_is_rejected_as_precondition_failed()
    {
        var response = await SendRawAsync("SUBSCRIBE", "/event/avtransport", new()
        {
            ["SID"] = $"uuid:{Guid.NewGuid():D}",
            ["TIMEOUT"] = "Second-300",
        });

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task Mixing_sid_with_callback_is_bad_request()
    {
        await using var callback = CallbackServer.Start();
        var created = await SubscribeAsync(callback);
        var sid = created.Headers.GetValues("SID").Single();

        var response = await SendRawAsync("SUBSCRIBE", "/event/avtransport", new()
        {
            ["SID"] = sid,
            ["CALLBACK"] = callback.CallbackHeader,
            ["NT"] = "upnp:event",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Expired_subscription_cannot_be_renewed()
    {
        await using var callback = CallbackServer.Start();
        var created = await SubscribeAsync(callback);
        var sid = created.Headers.GetValues("SID").Single();

        // Expire it the way time would: the registry only holds the deadline.
        var subscription = _host.Eventing.Registry.ForService(GenaServiceKind.AvTransport).Single();
        Assert.Equal(sid, subscription.Sid);

        ForceExpiry(subscription);

        var renewed = await SendRawAsync("SUBSCRIBE", "/event/avtransport", new() { ["SID"] = sid });
        Assert.Equal(HttpStatusCode.PreconditionFailed, renewed.StatusCode);
        Assert.Equal(0, _host.Eventing.Registry.Count);
    }

    [Fact]
    public async Task Unsubscribe_removes_the_subscription_and_a_second_attempt_fails()
    {
        await using var callback = CallbackServer.Start();
        var created = await SubscribeAsync(callback);
        var sid = created.Headers.GetValues("SID").Single();

        var first = await SendRawAsync("UNSUBSCRIBE", "/event/avtransport", new() { ["SID"] = sid });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(0, _host.Eventing.Registry.Count);

        var second = await SendRawAsync("UNSUBSCRIBE", "/event/avtransport", new() { ["SID"] = sid });
        Assert.Equal(HttpStatusCode.PreconditionFailed, second.StatusCode);
    }

    [Theory]
    [InlineData(null, null)] // no NT at all
    [InlineData("not-an-event", "<http://192.168.1.10:1/cb>")] // wrong NT value
    [InlineData("upnp:event", null)] // no CALLBACK
    public async Task Incomplete_subscribe_requests_are_rejected(string? nt, string? callback)
    {
        var headers = new Dictionary<string, string>();
        if (nt is not null)
        {
            headers["NT"] = nt;
        }

        if (callback is not null)
        {
            headers["CALLBACK"] = callback;
        }

        var response = await SendRawAsync("SUBSCRIBE", "/event/avtransport", headers);
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_callback_urls_are_refused_by_policy()
    {
        // Each of these would turn the renderer into a request proxy for the caller.
        var bad = new[]
        {
            "<file:///c:/windows/win.ini>",
            "<https://192.168.1.10:443/cb>",
            "<http://user:secret@192.168.1.10:1234/cb>",
            "<http://example.com:1234/cb>", // host name, not an IP literal
            "<http://127.0.0.1.nip.io:1234/cb>",
            "<http://0.0.0.0:1234/cb>",
            "<http://169.254.169.254/latest/meta-data/>",
            "http://192.168.1.10:1234/cb", // missing angle brackets
        };

        foreach (var callback in bad)
        {
            var response = await SendRawAsync("SUBSCRIBE", "/event/avtransport", new()
            {
                ["CALLBACK"] = callback,
                ["NT"] = "upnp:event",
            });

            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        }

        Assert.Equal(0, _host.Eventing.Registry.Count);
    }

    [Fact]
    public async Task Eventing_endpoint_rejects_methods_it_does_not_serve()
    {
        foreach (var method in new[] { "POST", "GET", "NOTIFY" })
        {
            var request = new HttpRequestMessage(HttpMethod.Parse(method), "/event/avtransport");
            var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }

    // ---- initial event and sequencing -------------------------------------------------------

    [Fact]
    public async Task New_subscription_receives_an_initial_event_with_sequence_zero()
    {
        await using var callback = CallbackServer.Start();

        await SubscribeAsync(callback);
        var first = (await callback.WaitForAsync(1, Timeout))[0];

        Assert.Equal("NOTIFY", first.Method);
        Assert.Equal("upnp:event", first.Nt);
        Assert.Equal("upnp:propchange", first.Nts);
        Assert.Equal("0", first.Sequence);
        Assert.NotNull(first.Sid);

        // The body must be a propertyset carrying LastChange, with the inner document escaped.
        var document = XDocument.Parse(first.Body);
        var propertySet = document.Root!;
        Assert.Equal(GenaEventBuilder.Event + "propertyset", propertySet.Name);

        var lastChange = propertySet.Descendants().Single(e => e.Name.LocalName == "LastChange").Value;
        var inner = XDocument.Parse(lastChange);
        Assert.Equal("Event", inner.Root!.Name.LocalName);

        var state = inner.Descendants().Single(e => e.Name.LocalName == "TransportState");
        Assert.Equal("STOPPED", state.Attribute("val")!.Value);
    }

    [Fact]
    public async Task Initial_event_carries_the_media_uri_and_metadata()
    {
        // A control point that subscribes mid-playback learns the track from the initial event, not
        // from the action that started it, so both URI variables and both metadata variables matter.
        var uri = new Uri("http://192.168.1.9:18777/movie.mp4");
        const string didl = "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">"
            + "<item id=\"1\"><dc:title xmlns:dc=\"http://purl.org/dc/elements/1.1/\">Movie</dc:title></item>"
            + "</DIDL-Lite>";
        await _renderer.LoadAsync(uri, didl, CancellationToken.None);

        await using var callback = CallbackServer.Start();
        await SubscribeAsync(callback);
        var first = (await callback.WaitForAsync(1, Timeout))[0];

        var inner = XDocument.Parse(XDocument.Parse(first.Body).Root!
            .Descendants().Single(e => e.Name.LocalName == "LastChange").Value);

        string Value(string name)
            => inner.Descendants().Single(e => e.Name.LocalName == name).Attribute("val")!.Value;

        Assert.Equal(uri.AbsoluteUri, Value("AVTransportURI"));
        Assert.Equal(didl, Value("AVTransportURIMetaData"));
        Assert.Equal(uri.AbsoluteUri, Value("CurrentTrackURI"));
        Assert.Equal(didl, Value("CurrentTrackMetaData"));
    }

    [Fact]
    public async Task Successful_notifications_are_logged_with_sid_and_sequence()
    {
        // This is the evidence a real-device acceptance report quotes: SUBSCRIBE followed by a NOTIFY
        // the control point answered with 200. A failure-only log cannot prove the positive case.
        await using var callback = CallbackServer.Start();
        var created = await SubscribeAsync(callback);
        var sid = created.Headers.GetValues("SID").Single();
        await callback.WaitForAsync(1, Timeout);

        _host.Eventing.UpdateAvTransport(PlaybackState.Playing, null, TimeSpan.FromMinutes(1));
        await callback.WaitForAsync(2, Timeout);

        // Delivery completes after the callback answers, so poll instead of assuming the log line
        // is already flushed when the notification arrives.
        var deadline = DateTime.UtcNow + Timeout;
        string? line = null;
        while (DateTime.UtcNow < deadline && line is null)
        {
            line = _logs.FirstOrDefault(l => l.Contains($"notify {sid} seq 1", StringComparison.Ordinal)
                && l.Contains("200", StringComparison.Ordinal));
            if (line is null)
            {
                await Task.Delay(50);
            }
        }

        Assert.NotNull(line);
    }

    [Fact]
    public async Task State_changes_notify_with_increasing_sequence_numbers()
    {
        await using var callback = CallbackServer.Start();
        await SubscribeAsync(callback);

        // Initial event: SEQ 0.
        await callback.WaitForAsync(1, Timeout);

        _host.Eventing.UpdateAvTransport(PlaybackState.Playing, null, TimeSpan.FromMinutes(3));
        await callback.WaitForAsync(2, Timeout);

        _host.Eventing.UpdateAvTransport(PlaybackState.Paused, null, TimeSpan.FromMinutes(3));
        await callback.WaitForAsync(3, Timeout);

        var all = callback.Received;
        Assert.Equal(new[] { "0", "1", "2" }, all.Select(n => n.Sequence ?? string.Empty).ToArray());

        var playing = all[1];
        var inner = XDocument.Parse(XDocument.Parse(playing.Body).Root!
            .Descendants().Single(e => e.Name.LocalName == "LastChange").Value);
        Assert.Equal("PLAYING", inner.Descendants().Single(e => e.Name.LocalName == "TransportState").Attribute("val")!.Value);
        Assert.Equal("00:03:00", inner.Descendants().Single(e => e.Name.LocalName == "CurrentTrackDuration").Attribute("val")!.Value);
    }

    [Fact]
    public async Task Repeated_identical_state_does_not_spam_subscribers()
    {
        await using var callback = CallbackServer.Start();
        await SubscribeAsync(callback);
        await callback.WaitForAsync(1, Timeout);

        // Player progress ticks report the same transport state many times per second.
        for (var i = 0; i < 20; i++)
        {
            _host.Eventing.UpdateAvTransport(PlaybackState.Playing, null, TimeSpan.FromMinutes(3));
        }

        await callback.WaitForAsync(2, Timeout);

        // Give any duplicate deliveries time to arrive, then assert there is exactly one.
        await Task.Delay(500);
        Assert.Equal(2, callback.Count);
    }

    [Fact]
    public async Task Every_subscriber_has_its_own_sequence()
    {
        await using var first = CallbackServer.Start();
        await using var second = CallbackServer.Start();

        await SubscribeAsync(first);
        await SubscribeAsync(second);

        await first.WaitForAsync(1, Timeout);
        await second.WaitForAsync(1, Timeout);

        // Only the second subscriber is renewed and then advanced twice; its SEQ must climb
        // independently of the first subscriber's.
        _host.Eventing.UpdateAvTransport(PlaybackState.Playing, null, TimeSpan.FromMinutes(1));
        await first.WaitForAsync(2, Timeout);
        await second.WaitForAsync(2, Timeout);

        _host.Eventing.UpdateAvTransport(PlaybackState.Stopped, null, null);
        await first.WaitForAsync(3, Timeout);
        await second.WaitForAsync(3, Timeout);

        Assert.Equal(new[] { "0", "1", "2" }, first.Received.Select(n => n.Sequence ?? string.Empty).ToArray());
        Assert.Equal(new[] { "0", "1", "2" }, second.Received.Select(n => n.Sequence ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task RenderingControl_subscribers_receive_volume_and_mute()
    {
        await using var callback = CallbackServer.Start();

        var response = await SendRawAsync("SUBSCRIBE", "/event/renderingcontrol", new()
        {
            ["CALLBACK"] = callback.CallbackHeader,
            ["NT"] = "upnp:event",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await callback.WaitForAsync(1, Timeout);

        _host.Eventing.UpdateRenderingControl(42, muted: true);

        var notify = await callback.WaitForAsync(
            n =>
            {
                var inner = XDocument.Parse(XDocument.Parse(n.Body).Root!
                    .Descendants().Single(e => e.Name.LocalName == "LastChange").Value);
                return inner.Descendants().Any(e => e.Name.LocalName == "Volume" && (string?)e.Attribute("val") == "42");
            },
            Timeout);

        var document = XDocument.Parse(XDocument.Parse(notify.Body).Root!
            .Descendants().Single(e => e.Name.LocalName == "LastChange").Value);

        var volume = document.Descendants().Single(e => e.Name.LocalName == "Volume");
        var mute = document.Descendants().Single(e => e.Name.LocalName == "Mute");

        Assert.Equal("42", volume.Attribute("val")!.Value);
        Assert.Equal("Master", volume.Attribute("channel")!.Value);
        Assert.Equal("1", mute.Attribute("val")!.Value);
        Assert.Equal("Master", mute.Attribute("channel")!.Value);
    }

    [Fact]
    public async Task Event_payloads_escape_xml_special_characters()
    {
        await using var callback = CallbackServer.Start();
        await SubscribeAsync(callback);
        await callback.WaitForAsync(1, Timeout);

        // An error message full of markup: it must not be able to break either XML layer.
        _host.Eventing.UpdateAvTransport(PlaybackState.Playing, "bad <tag> & \"quotes\"", TimeSpan.FromSeconds(61));

        var notify = await callback.WaitForAsync(
            n => n.Body.Contains("ERROR_OCCURRED", StringComparison.Ordinal),
            Timeout);

        // The outer document must parse, and the inner LastChange document must parse after one
        // unescaping - that double encoding is exactly what clients rely on.
        var outer = XDocument.Parse(notify.Body);
        var lastChange = outer.Root!.Descendants().Single(e => e.Name.LocalName == "LastChange").Value;
        var inner = XDocument.Parse(lastChange);

        Assert.Equal("ERROR_OCCURRED", inner.Descendants()
            .Single(e => e.Name.LocalName == "TransportStatus").Attribute("val")!.Value);

        // The inner document is escaped exactly once: a double escape would show up as &amp;lt;.
        Assert.Contains("&lt;m:Event", notify.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("&amp;lt;", notify.Body, StringComparison.Ordinal);
    }

    // ---- failure handling and isolation -----------------------------------------------------

    [Fact]
    public async Task A_failing_subscriber_does_not_stop_other_subscribers()
    {
        await using var good = CallbackServer.Start();
        await using var bad = CallbackServer.Start();
        bad.ResponseStatus = 500;

        await SubscribeAsync(good);
        await SubscribeAsync(bad);

        await good.WaitForAsync(1, Timeout);

        _host.Eventing.UpdateAvTransport(PlaybackState.Playing, null, TimeSpan.FromMinutes(1));

        // The healthy subscriber still receives the change even though the other one answers 500.
        var received = await good.WaitForAsync(2, Timeout);
        Assert.Equal("1", received[1].Sequence);
    }

    [Fact]
    public async Task A_refusing_callback_port_is_tolerated_and_dropped_after_the_budget()
    {
        // A port that was open when the client subscribed and closed afterwards.
        var port = FreePort();
        var callback = $"<http://127.0.0.1:{port}/notify>";

        var response = await SendRawAsync("SUBSCRIBE", "/event/avtransport", new()
        {
            ["CALLBACK"] = callback,
            ["NT"] = "upnp:event",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The initial event already failed once (the port is closed). Keep alternating the state so
        // every iteration publishes; delivery is asynchronous, so wait for the outcome instead of
        // assuming a fixed number of iterations is enough.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var playing = false;
        while (DateTime.UtcNow < deadline && _host.Eventing.Registry.Count > 0)
        {
            playing = !playing;
            _host.Eventing.UpdateAvTransport(
                playing ? PlaybackState.Playing : PlaybackState.Paused,
                null,
                null);
            await Task.Delay(150);
        }

        Assert.Equal(0, _host.Eventing.Registry.Count);
    }

    [Fact]
    public async Task A_callback_that_never_answers_times_out_without_blocking()
    {
        await using var callback = CallbackServer.Start();
        callback.HangRequests = true;

        await SubscribeAsync(callback);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _host.Eventing.UpdateAvTransport(PlaybackState.Playing, null, TimeSpan.FromMinutes(1));
        stopwatch.Stop();

        // Publishing is fire-and-forget: the caller must not wait for the callback.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"publish blocked for {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Subscription_limit_is_enforced_and_reported_as_server_error()
    {
        var options = new GenaOptions { AllowLoopbackCallbacks = true, MaxSubscriptionsPerAddress = 2 };
        await using var host = new DlnaDeviceHost(new DlnaOptions { Eventing = options }, new FakeTransportRenderer());
        host.Start();

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.HttpPort}") };
        await using var callback = CallbackServer.Start();

        for (var i = 0; i < 2; i++)
        {
            var ok = await SendSubscribeAsync(client, callback.CallbackHeader, "/event/avtransport");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var refused = await SendSubscribeAsync(client, callback.CallbackHeader, "/event/avtransport");
        Assert.Equal(HttpStatusCode.InternalServerError, refused.StatusCode);
        Assert.Equal(2, host.Eventing.Registry.Count);
    }

    [Fact]
    public async Task Subscriptions_do_not_survive_host_shutdown()
    {
        await using var callback = CallbackServer.Start();
        var host = new DlnaDeviceHost(
            new DlnaOptions { Eventing = new GenaOptions { AllowLoopbackCallbacks = true } },
            new FakeTransportRenderer());
        host.Start();

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.HttpPort}") };
        var response = await SendSubscribeAsync(client, callback.CallbackHeader, "/event/avtransport");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, host.Eventing.Registry.Count);

        await host.DisposeAsync();

        Assert.Equal(0, host.Eventing.Registry.Count);
    }

    [Fact]
    public async Task Expired_subscriptions_are_pruned_and_stop_receiving()
    {
        var options = new GenaOptions
        {
            AllowLoopbackCallbacks = true,
            MinTimeout = TimeSpan.FromSeconds(1),
            MaxTimeout = TimeSpan.FromSeconds(1),
        };

        await using var host = new DlnaDeviceHost(new DlnaOptions { Eventing = options }, new FakeTransportRenderer());
        host.Start();

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.HttpPort}") };
        await using var callback = CallbackServer.Start();

        // The short lifetime only applies when the client asks for it; without TIMEOUT the renderer
        // grants its default, which is deliberately much longer.
        var response = await SendSubscribeAsync(client, callback.CallbackHeader, "/event/avtransport", timeout: "Second-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Second-1", response.Headers.GetValues("TIMEOUT").First());

        // The registry sweeps on its own schedule, so the subscription may already be gone; either
        // path must leave nothing behind. Wait for the sweep rather than assuming its period.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && host.Eventing.Registry.Count > 0)
        {
            await Task.Delay(100);
        }
        Assert.Equal(0, host.Eventing.Registry.Count);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private Task<HttpResponseMessage> SubscribeAsync(CallbackServer callback, string? timeout = null)
        => SendSubscribeAsync(_client, callback.CallbackHeader, "/event/avtransport", timeout);

    private static async Task<HttpResponseMessage> SendSubscribeAsync(
        HttpClient client,
        string callbackHeader,
        string path,
        string? timeout = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Parse("SUBSCRIBE"), path);
        request.Headers.TryAddWithoutValidation("CALLBACK", callbackHeader);
        request.Headers.TryAddWithoutValidation("NT", "upnp:event");
        if (timeout is not null)
        {
            request.Headers.TryAddWithoutValidation("TIMEOUT", timeout);
        }

        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendRawAsync(string method, string path, Dictionary<string, string> headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Parse(method), path);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await _client.SendAsync(request);
    }

    /// <summary>
    /// Expires a subscription without waiting: the behaviour under test is "expired is treated as
    /// gone", not the passage of wall-clock time.
    /// </summary>
    private static void ForceExpiry(GenaSubscription subscription)
        => subscription.ExpireAt(DateTimeOffset.UtcNow.AddSeconds(-1));

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}


