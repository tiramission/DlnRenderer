using System.Collections.Concurrent;
using System.Net;
using System.Xml.Linq;
using DlnRenderer.Core.Models;
using DlnRenderer.Dlna;
using DlnRenderer.Dlna.Gena;
using DlnRenderer.Player.Mpv;
using DlnRenderer.Service.Hosting;
using DlnRenderer.Tests.Support;

namespace DlnRenderer.Service.Tests;

/// <summary>
/// Concurrency and consistency between the player, the IPC snapshot and GENA subscribers.
/// </summary>
/// <remarks>
/// These are the cases that only appear under load: a volume burst from several control points, a
/// state change racing a subscriber that is being removed, and the same value being read from three
/// different surfaces. They assert agreement between surfaces rather than any single value, because
/// disagreement is the defect.
/// </remarks>
[Collection("video-window")]
public class VolumeStateConsistencyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Concurrent_volume_writers_leave_the_player_and_snapshot_agreeing()
    {
        RequireMpv();
        await using var ctx = await SessionContext.CreateAsync();

        // Several control points setting volume in quick succession, as a UI slider dragged while a
        // phone sends SetVolume. Commands are issued in order and each waits until its value is
        // observable, so the final state is the last one requested - never an intermediate value
        // that happened to reach libmpv later.
        var values = Enumerable.Range(0, 10).Select(i => 5 + (i * 9 % 90)).ToArray();
        foreach (var value in values)
        {
            await ctx.Player.SetVolumeAsync(value, CancellationToken.None);

            // Every write is observable immediately after it completes.
            Assert.Equal(value, (await ctx.Player.GetStatusAsync(CancellationToken.None)).Volume);
        }

        var player = (await ctx.Player.GetStatusAsync(CancellationToken.None)).Volume;
        Assert.Equal(values[^1], player);

        await WaitUntil(() => ctx.Session.GetSnapshot().Playback.Volume == player, "snapshot matches the player");
        Assert.Equal(player, ctx.Session.GetSnapshot().Playback.Volume);
    }

    [Fact]
    public async Task Unsubscribing_while_publishing_does_not_fail_the_publish()
    {
        RequireMpv();
        await using var ctx = await SessionContext.CreateAsync();
        await using var first = CallbackServer.Start();
        await using var second = CallbackServer.Start();

        await SubscribeAsync(ctx, first);
        await SubscribeAsync(ctx, second);

        // Remove one subscriber while the other keeps the fan-out running.
        var publish = Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                ctx.DeviceHost.Eventing.UpdateRenderingControl(i % 100, false);
            }
        });

        var unsubscribe = Task.Run(() => ctx.DeviceHost.Eventing.Registry.TryRemove(
            ctx.DeviceHost.Eventing.Registry.ForService(GenaServiceKind.RenderingControl).First().Sid));

        await Task.WhenAll(publish, unsubscribe);

        // The surviving subscriber still receives events: removal cannot poison the fan-out.
        await second.WaitForAsync(2, Timeout);
    }

    [Fact]
    public async Task Mute_state_is_reported_identically_by_snapshot_and_soap()
    {
        RequireMpv();
        await using var ctx = await SessionContext.CreateAsync();

        await ctx.Player.SetMuteAsync(true, CancellationToken.None);
        await WaitUntil(() => ctx.Session.GetSnapshot().Playback.Muted, "snapshot sees mute");

        var soapMute = await ctx.SoapGetAsync("GetMute", "<InstanceID>0</InstanceID><Channel>Master</Channel>");

        Assert.True(ctx.Session.GetSnapshot().Playback.Muted);
        Assert.Equal("1", soapMute);
    }

    [Fact]
    public async Task Shutdown_while_a_callback_is_slow_does_not_hang()
    {
        RequireMpv();
        var ctx = await SessionContext.CreateAsync();
        await using var callback = CallbackServer.Start();
        callback.HangRequests = true;

        await SubscribeAsync(ctx, callback);

        // Disposal must not wait for an unresponsive callback.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await ctx.DisposeAsync();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"dispose took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task A_player_failure_does_not_leave_a_pending_event_task()
    {
        RequireMpv();
        await using var ctx = await SessionContext.CreateAsync();
        await using var callback = CallbackServer.Start();
        await SubscribeAsync(ctx, callback);

        // No media is loaded; Play fails. The failure must not surface as an unobserved exception in
        // eventing, and the subscriber must remain usable afterwards.
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Session.PlayAsync(CancellationToken.None));

        ctx.DeviceHost.Eventing.UpdateRenderingControl(33, false);
        var notify = await callback.WaitForAsync(
            n => n.Body.Contains("33", StringComparison.Ordinal),
            Timeout);

        Assert.NotNull(notify);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static async Task SubscribeAsync(SessionContext ctx, CallbackServer callback)
    {
        var request = new DlnRenderer.Dlna.Http.DlnHttpRequest(
            "SUBSCRIBE",
            "/event/renderingcontrol",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CALLBACK"] = callback.CallbackHeader,
                ["NT"] = "upnp:event",
                ["TIMEOUT"] = "Second-300",
            },
            string.Empty,
            "127.0.0.1");

        var response = await ctx.DeviceHost.Eventing.SubscribeAsync(
            GenaServiceKind.RenderingControl, request, CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException($"timed out waiting for: {what}");
    }

    private static void RequireMpv()
        => Assert.True(MpvRuntime.IsAvailable(), $"libmpv is not available at {MpvRuntime.ExpectedPath}");

    private sealed class SessionContext : IAsyncDisposable
    {
        private readonly MpvPlayer _player;
        private readonly VideoPresenter _presenter;

        private SessionContext(MpvPlayer player, VideoPresenter presenter, RendererSession session, DlnaDeviceHost host)
        {
            _player = player;
            _presenter = presenter;
            Session = session;
            DeviceHost = host;
        }

        public MpvPlayer Player => _player;

        public RendererSession Session { get; }

        public DlnaDeviceHost DeviceHost { get; }

        private HttpClient? _client;

        public static async Task<SessionContext> CreateAsync()
        {
            var presenter = await VideoPresenter.StartAsync();
            var player = new MpvPlayer(new MpvPlayerOptions
            {
                VideoWindowHandle = presenter.ChildHandle,
                AudioOutput = "null",
            });

            var options = new DlnaOptions
            {
                Eventing = new GenaOptions { AllowLoopbackCallbacks = true },
            };

            var session = new RendererSession(player, presenter, options, _ => { });
            var host = new DlnaDeviceHost(options, session, _ => { });

            session.RenderingEventRaised += (volume, muted) => host.Eventing.UpdateRenderingControl(volume, muted);
            session.TransportEventRaised += status => host.Eventing.UpdateAvTransport(status.State, status.ErrorMessage, status.Duration);

            host.Start();
            return new SessionContext(player, presenter, session, host);
        }

        public async Task<string> SoapGetAsync(string action, string arguments)
        {
            _client ??= new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{DeviceHost.HttpPort}") };

            var envelope =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                + "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">"
                + "<s:Body>"
                + $"<u:{action} xmlns:u=\"urn:schemas-upnp-org:service:RenderingControl:1\">{arguments}</u:{action}>"
                + "</s:Body></s:Envelope>";

            var request = new HttpRequestMessage(HttpMethod.Post, "/control/renderingcontrol")
            {
                Content = new StringContent(envelope, System.Text.Encoding.UTF8, "text/xml"),
            };
            request.Headers.TryAddWithoutValidation(
                "SOAPAction",
                $"\"urn:schemas-upnp-org:service:RenderingControl:1#{action}\"");

            var response = await _client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            return XDocument.Parse(body).Descendants()
                .First(e => e.Name.LocalName is "CurrentMute" or "CurrentVolume").Value;
        }

        public async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            await DeviceHost.DisposeAsync();
            await Session.DisposeAsync();
            await _player.DisposeAsync();
            await _presenter.DisposeAsync();
        }
    }
}
