using System.Net;
using System.Xml.Linq;
using DlnRenderer.Core.Models;
using DlnRenderer.Dlna;
using DlnRenderer.Dlna.Gena;
using DlnRenderer.Dlna.Soap;
using DlnRenderer.Player.Mpv;
using DlnRenderer.Service.Hosting;
using DlnRenderer.Tests.Support;

namespace DlnRenderer.Service.Tests;

/// <summary>
/// Volume and mute driven through the real session and real libmpv, observed by GENA subscribers.
/// </summary>
/// <remarks>
/// The point of these tests is that nothing is faked: the value a subscriber receives is the one the
/// player reports, and a SOAP SetVolume changes the player itself rather than a service field.
/// Requires libmpv; skipped when it is absent, like the player's own integration tests.
/// </remarks>
[Collection("video-window")]
public class RenderingControlIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [MpvFact]
    public async Task SetVolume_over_soap_changes_the_real_player_volume()
    {
        await using var ctx = await SessionContext.CreateAsync();

        var soap = await ctx.SoapVolumeAsync(volume: 31);

        Assert.Equal(HttpStatusCode.OK, soap.StatusCode);

        var status = await ctx.Player.GetStatusAsync(CancellationToken.None);
        Assert.Equal(31, status.Volume);
    }

    [MpvFact]
    public async Task GetVolume_reports_the_player_value_after_a_direct_change()
    {
        await using var ctx = await SessionContext.CreateAsync();

        // Change the player behind the protocol layer's back, as another path could.
        await ctx.Player.SetVolumeAsync(73, CancellationToken.None);

        var reported = await ctx.SoapGetVolumeAsync();

        Assert.Equal("73", reported);
    }

    [MpvFact]
    public async Task SetMute_over_soap_mutes_the_real_player()
    {
        await using var ctx = await SessionContext.CreateAsync();

        await ctx.SoapMuteAsync(muted: true);
        Assert.True((await ctx.Player.GetStatusAsync(CancellationToken.None)).Muted);

        await ctx.SoapMuteAsync(muted: false);
        Assert.False((await ctx.Player.GetStatusAsync(CancellationToken.None)).Muted);
    }

    [MpvFact]
    public async Task Volume_changes_reach_a_gena_subscriber()
    {
        await using var ctx = await SessionContext.CreateAsync();
        await using var callback = CallbackServer.Start();

        var subscribe = await ctx.DeviceHost.Eventing.SubscribeAsync(
            GenaServiceKind.RenderingControl,
            SubscribeRequest(callback),
            CancellationToken.None);
        Assert.Equal(200, subscribe.StatusCode);

        await callback.WaitForAsync(1, Timeout);

        await ctx.SoapVolumeAsync(volume: 19);

        var notify = await callback.WaitForAsync(
            n =>
            {
                var inner = InnerEvent(n.Body);
                return inner.Descendants().Any(e => e.Name.LocalName == "Volume" && (string?)e.Attribute("val") == "19");
            },
            Timeout);

        Assert.NotNull(notify.Sid);
    }

    [MpvFact]
    public async Task Snapshot_volume_matches_the_player_after_a_change()
    {
        await using var ctx = await SessionContext.CreateAsync();

        await ctx.SoapVolumeAsync(volume: 55);
        await WaitUntil(
            () => ctx.Session.GetSnapshot().Playback.Volume == 55,
            "snapshot volume follows the player");
    }

    [MpvFact]
    public async Task Volume_and_mute_survive_stopping_and_switching_media()
    {
        var clip = TestMediaPath();

        await using var ctx = await SessionContext.CreateAsync();
        await ctx.SoapVolumeAsync(volume: 27);
        await ctx.SoapMuteAsync(muted: true);

        await ctx.Session.LoadAsync(new Uri(clip), null, CancellationToken.None);
        await ctx.Session.StopAsync(CancellationToken.None);
        await ctx.Session.LoadAsync(new Uri(clip), null, CancellationToken.None);

        // The player is one continuous mpv instance; volume and mute belong to it, not to a medium.
        var status = await ctx.Player.GetStatusAsync(CancellationToken.None);
        Assert.Equal(27, status.Volume);
        Assert.True(status.Muted);
    }

    [MpvFact]
    public async Task Volume_can_be_set_while_playing_and_while_stopped()
    {
        var clip = TestMediaPath();

        await using var ctx = await SessionContext.CreateAsync();

        // Stopped: no media loaded at all.
        await ctx.SoapVolumeAsync(volume: 11);
        Assert.Equal(11, (await ctx.Player.GetStatusAsync(CancellationToken.None)).Volume);

        // Playing.
        await ctx.Session.LoadAsync(new Uri(clip), null, CancellationToken.None);
        await ctx.Session.PlayAsync(CancellationToken.None);
        await ctx.SoapVolumeAsync(volume: 66);
        Assert.Equal(66, (await ctx.Player.GetStatusAsync(CancellationToken.None)).Volume);

        await ctx.Session.StopAsync(CancellationToken.None);
        await ctx.SoapVolumeAsync(volume: 12);
        Assert.Equal(12, (await ctx.Player.GetStatusAsync(CancellationToken.None)).Volume);
    }

    [MpvFact]
    public async Task Out_of_range_volume_over_soap_faults_and_leaves_the_player_alone()
    {
        await using var ctx = await SessionContext.CreateAsync();

        var before = (await ctx.Player.GetStatusAsync(CancellationToken.None)).Volume;
        var response = await ctx.SoapAsync(
            "SetVolume",
            $"<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>150</DesiredVolume>");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("<errorCode>601</errorCode>", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(before, (await ctx.Player.GetStatusAsync(CancellationToken.None)).Volume);
    }

    [MpvFact]
    public async Task Soap_volume_change_reaches_a_subscriber_with_the_player_value()
    {
        await using var ctx = await SessionContext.CreateAsync();
        await using var callback = CallbackServer.Start();

        var subscribe = await ctx.DeviceHost.Eventing.SubscribeAsync(
            GenaServiceKind.RenderingControl,
            SubscribeRequest(callback),
            CancellationToken.None);
        Assert.Equal(200, subscribe.StatusCode);

        await callback.WaitForAsync(1, Timeout);

        await ctx.SoapVolumeAsync(volume: 84);

        // The event must carry the value the player actually holds, not the number that was sent.
        var notify = await callback.WaitForAsync(
            n =>
            {
                var inner = InnerEvent(n.Body);
                return inner.Descendants().Any(e => e.Name.LocalName == "Volume" && (string?)e.Attribute("val") == "84");
            },
            Timeout);

        Assert.Equal("84", (await ctx.Player.GetStatusAsync(CancellationToken.None)).Volume.ToString());

        // SEQ keeps increasing per subscription, so a client can order its events.
        var sequences = callback.Received.Select(n => n.Sequence).ToArray();
        Assert.Equal("0", sequences[0]);
        Assert.Equal(sequences.Distinct().Count(), sequences.Length);
    }

    [MpvFact]
    public async Task Mute_change_reaches_a_subscriber()
    {
        await using var ctx = await SessionContext.CreateAsync();
        await using var callback = CallbackServer.Start();

        await ctx.DeviceHost.Eventing.SubscribeAsync(
            GenaServiceKind.RenderingControl,
            SubscribeRequest(callback),
            CancellationToken.None);

        await callback.WaitForAsync(1, Timeout);

        await ctx.SoapMuteAsync(muted: true);

        var notify = await callback.WaitForAsync(
            n =>
            {
                var inner = InnerEvent(n.Body);
                return inner.Descendants().Any(e => e.Name.LocalName == "Mute" && (string?)e.Attribute("val") == "1");
            },
            Timeout);

        Assert.NotNull(notify.Sid);
    }

    // ---- helpers ---------------------------------------------------------------------------


    private static DlnRenderer.Dlna.Http.DlnHttpRequest SubscribeRequest(CallbackServer callback)
        => new(
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

    private static XElement InnerEvent(string body)
        => XDocument.Parse(XDocument.Parse(body).Root!
            .Descendants().Single(e => e.Name.LocalName == "LastChange").Value).Root!;

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException($"timed out waiting for: {what}");
    }

    /// <summary>
    /// Locates the shared media fixture, which lives in the player test project.
    /// </summary>
    /// <remarks>
    /// The file is not copied into this project's output: it is only needed by two integration tests,
    /// and a copy would be a second thing to keep in sync. Walking up to the repository root keeps
    /// the path correct regardless of configuration.
    /// </remarks>
    private static string TestMediaPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "DlnRenderer.Player.Tests", "Media", "sample-video.mp4");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "The shared media fixture tests/DlnRenderer.Player.Tests/Media/sample-video.mp4 was not found.");
    }

    private sealed class SessionContext : IAsyncDisposable
    {
        private readonly VideoPresenter _presenter;

        private SessionContext(MpvPlayer player, VideoPresenter presenter, RendererSession session, DlnaDeviceHost host)
        {
            Player = player;
            _presenter = presenter;
            Session = session;
            DeviceHost = host;
        }

        public MpvPlayer Player { get; }

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

        public async Task<HttpResponseMessage> SoapVolumeAsync(int volume)
            => await SoapAsync(
                "SetVolume",
                $"<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>{volume}</DesiredVolume>");

        public async Task<HttpResponseMessage> SoapMuteAsync(bool muted)
            => await SoapAsync(
                "SetMute",
                $"<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredMute>{(muted ? "1" : "0")}</DesiredMute>");

        public async Task<string> SoapGetVolumeAsync()
        {
            var response = await SoapAsync("GetVolume", "<InstanceID>0</InstanceID><Channel>Master</Channel>");
            var body = await response.Content.ReadAsStringAsync();
            var document = XDocument.Parse(body);
            return document.Descendants().Single(e => e.Name.LocalName == "CurrentVolume").Value;
        }

        public async Task<HttpResponseMessage> SoapAsync(string action, string arguments)
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

            return await _client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            await DeviceHost.DisposeAsync();
            await Session.DisposeAsync();
            await Player.DisposeAsync();
            await _presenter.DisposeAsync();
        }
    }

}
