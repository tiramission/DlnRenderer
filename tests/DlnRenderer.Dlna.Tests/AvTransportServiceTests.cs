using System.Xml.Linq;
using DlnRenderer.Core.Models;
using DlnRenderer.Dlna.AvTransport;
using DlnRenderer.Dlna.Soap;
using DlnRenderer.Dlna.Tests.Support;

namespace DlnRenderer.Dlna.Tests;

public class AvTransportServiceTests
{
    private static readonly XNamespace Avt = SoapNamespaces.AvTransportServiceType;

    private static (AvTransportService Service, FakeTransportRenderer Renderer) Create()
    {
        var renderer = new FakeTransportRenderer();
        return (new AvTransportService(renderer, _ => { }), renderer);
    }

    private static (string Action, Dictionary<string, string> Values) ParseResponse(string xml, string action)
    {
        var document = XDocument.Parse(xml);
        var body = document.Root!.Elements().Single(e => e.Name.LocalName == "Body");
        var response = body.Elements().Single();
        Assert.Equal(action + "Response", response.Name.LocalName);
        Assert.Equal(Avt, response.Name.Namespace);

        return (action, response.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value));
    }

    private static async Task<UpnpException> FaultAsync(Func<Task> action)
        => await Assert.ThrowsAsync<UpnpException>(action);

    [Fact]
    public async Task SetAVTransportURI_stores_uri_and_metadata()
    {
        var (service, renderer) = Create();
        const string didl = "<DIDL-Lite><item><title>Clip</title></item></DIDL-Lite>";

        var response = await service.HandleAsync(
            SoapBody.AvTransport("SetAVTransportURI",
                ("InstanceID", "0"),
                ("CurrentURI", "http://192.168.1.5:8080/media/clip.mp4"),
                ("CurrentURIMetaData", didl)),
            CancellationToken.None);

        ParseResponse(response, "SetAVTransportURI");
        Assert.Equal(new Uri("http://192.168.1.5:8080/media/clip.mp4"), renderer.CurrentUri);
        Assert.Equal(didl, renderer.CurrentMetadata);
        Assert.Equal(["load:http://192.168.1.5:8080/media/clip.mp4"], renderer.Calls);
    }

    [Theory]
    [InlineData("rtsp://example.com/stream")]
    [InlineData("lavfi://testsrc")]
    [InlineData("edl://segment")]
    [InlineData("file:///C:/media/a.mp4")]
    [InlineData("C:\\media\\a.mp4")]
    [InlineData("/local/file.mp4")]
    [InlineData("relative/file.mp4")]
    public async Task SetAVTransportURI_rejects_non_media_uris(string uri)
    {
        var (service, renderer) = Create();
        var error = await FaultAsync(() => service.HandleAsync(
            SoapBody.AvTransport("SetAVTransportURI", ("InstanceID", "0"), ("CurrentURI", uri)),
            CancellationToken.None));

        Assert.Equal(UpnpErrorCode.ArgumentValueInvalid, error.Code);
        Assert.Empty(renderer.Calls);
    }

    [Fact]
    public async Task SetAVTransportURI_without_a_uri_faults_with_402()
    {
        var (service, renderer) = Create();
        var error = await FaultAsync(() => service.HandleAsync(
            SoapBody.AvTransport("SetAVTransportURI", ("InstanceID", "0"), ("CurrentURI", "")),
            CancellationToken.None));

        Assert.Equal(UpnpErrorCode.InvalidArgs, error.Code);
        Assert.Empty(renderer.Calls);
    }

    [Fact]
    public async Task SetAVTransportURI_maps_a_load_failure_to_ResourceNotFound()
    {
        var (service, renderer) = Create();
        renderer.FailLoad = true;

        var error = await FaultAsync(() => service.HandleAsync(
            SoapBody.AvTransport("SetAVTransportURI",
                ("InstanceID", "0"),
                ("CurrentURI", "http://192.168.1.5/missing.mp4")),
            CancellationToken.None));

        Assert.Equal(UpnpErrorCode.ResourceNotFound, error.Code);
        Assert.Null(renderer.CurrentUri);
    }

    [Theory]
    [InlineData("Play")]
    [InlineData("Pause")]
    [InlineData("Seek")]
    public async Task Transport_actions_require_media(string action)
    {
        var (service, _) = Create();
        var error = await FaultAsync(() => service.HandleAsync(
            SoapBody.AvTransport(
                action,
                ("InstanceID", "0"),
                ("Speed", "1"),
                ("Unit", "REL_TIME"),
                ("Target", "00:00:10")),
            CancellationToken.None));

        Assert.Equal(UpnpErrorCode.TransitionNotAvailable, error.Code);
    }

    [Fact]
    public async Task Play_drives_the_renderer_and_reports_playing()
    {
        var (service, renderer) = Create();
        await service.HandleAsync(
            SoapBody.AvTransport("SetAVTransportURI",
                ("InstanceID", "0"),
                ("CurrentURI", "http://192.168.1.5/clip.mp3")),
            CancellationToken.None);

        await service.HandleAsync(
            SoapBody.AvTransport("Play", ("InstanceID", "0"), ("Speed", "1")),
            CancellationToken.None);

        Assert.Equal(["load:http://192.168.1.5/clip.mp3", "play"], renderer.Calls);

        var info = ParseResponse(
            await service.HandleAsync(
                SoapBody.AvTransport("GetTransportInfo", ("InstanceID", "0")),
                CancellationToken.None),
            "GetTransportInfo");

        Assert.Equal("PLAYING", info.Values["CurrentTransportState"]);
        Assert.Equal("OK", info.Values["CurrentTransportStatus"]);
        Assert.Equal("1", info.Values["CurrentSpeed"]);
    }

    [Fact]
    public async Task Play_failure_is_reported_as_a_fault_not_a_faked_success()
    {
        var (service, renderer) = Create();
        await service.HandleAsync(
            SoapBody.AvTransport("SetAVTransportURI",
                ("InstanceID", "0"),
                ("CurrentURI", "http://192.168.1.5/clip.mp3")),
            CancellationToken.None);

        renderer.FailPlay = "the player refused to start";

        var error = await FaultAsync(() => service.HandleAsync(
            SoapBody.AvTransport("Play", ("InstanceID", "0"), ("Speed", "1")),
            CancellationToken.None));

        // A failed Play must surface as a UPnP error, never as a successful response.
        Assert.Equal(UpnpErrorCode.ActionFailed, error.Code);
    }

    [Fact]
    public async Task Play_rejects_a_non_normal_speed()
    {
        var (service, renderer) = Create();
        await service.HandleAsync(
            SoapBody.AvTransport("SetAVTransportURI",
                ("InstanceID", "0"),
                ("CurrentURI", "http://192.168.1.5/clip.mp3")),
            CancellationToken.None);

        var error = await FaultAsync(() => service.HandleAsync(
            SoapBody.AvTransport("Play", ("InstanceID", "0"), ("Speed", "2")),
            CancellationToken.None));

        Assert.Equal(UpnpErrorCode.ArgumentValueInvalid, error.Code);
        Assert.DoesNotContain("play", renderer.Calls);
    }

    [Theory]
    [InlineData(PlaybackState.Stopped, "STOPPED")]
    [InlineData(PlaybackState.Playing, "PLAYING")]
    [InlineData(PlaybackState.Paused, "PAUSED_PLAYBACK")]
    [InlineData(PlaybackState.Buffering, "TRANSITIONING")]
    [InlineData(PlaybackState.Transitioning, "TRANSITIONING")]
    public async Task GetTransportInfo_maps_player_states(PlaybackState state, string expected)
    {
        var (service, renderer) = Create();
        renderer.State = state;

        var info = ParseResponse(
            await service.HandleAsync(
                SoapBody.AvTransport("GetTransportInfo", ("InstanceID", "0")),
                CancellationToken.None),
            "GetTransportInfo");

        Assert.Equal(expected, info.Values["CurrentTransportState"]);
        Assert.Equal("OK", info.Values["CurrentTransportStatus"]);
    }

    [Fact]
    public async Task GetTransportInfo_reports_error_status_without_fake_playback()
    {
        var (service, renderer) = Create();
        renderer.State = PlaybackState.Error;
        renderer.Error = "unrecognized file format";

        var info = ParseResponse(
            await service.HandleAsync(
                SoapBody.AvTransport("GetTransportInfo", ("InstanceID", "0")),
                CancellationToken.None),
            "GetTransportInfo");

        Assert.Equal("STOPPED", info.Values["CurrentTransportState"]);
        Assert.Equal("ERROR_OCCURRED", info.Values["CurrentTransportStatus"]);
    }

    [Fact]
    public async Task GetPositionInfo_returns_real_position_duration_and_uri()
    {
        var (service, renderer) = Create();
        await service.HandleAsync(
            SoapBody.AvTransport("SetAVTransportURI",
                ("InstanceID", "0"),
                ("CurrentURI", "http://192.168.1.5/clip.mp4"),
                ("CurrentURIMetaData", "<DIDL-Lite><item><title>Clip</title></item></DIDL-Lite>")),
            CancellationToken.None);
        renderer.State = PlaybackState.Playing;
        renderer.Position = TimeSpan.FromSeconds(65);
        renderer.Duration = TimeSpan.FromMinutes(10);

        var info = ParseResponse(
            await service.HandleAsync(
                SoapBody.AvTransport("GetPositionInfo", ("InstanceID", "0")),
                CancellationToken.None),
            "GetPositionInfo");

        Assert.Equal("00:01:05", info.Values["RelTime"]);
        Assert.Equal("00:10:00", info.Values["TrackDuration"]);
        Assert.Equal("http://192.168.1.5/clip.mp4", info.Values["TrackURI"]);
        Assert.Equal("1", info.Values["Track"]);
        Assert.Contains("Clip", info.Values["TrackMetaData"]);
    }

    [Fact]
    public async Task GetPositionInfo_before_media_reports_no_track()
    {
        var (service, _) = Create();

        var info = ParseResponse(
            await service.HandleAsync(
                SoapBody.AvTransport("GetPositionInfo", ("InstanceID", "0")),
                CancellationToken.None),
            "GetPositionInfo");

        Assert.Equal("0", info.Values["Track"]);
        Assert.Equal(string.Empty, info.Values["TrackURI"]);
        Assert.Equal("00:00:00", info.Values["TrackDuration"]);
    }

    [Fact]
    public async Task GetPositionInfo_zeroes_position_when_stopped()
    {
        var (service, renderer) = Create();
        await service.HandleAsync(
            SoapBody.AvTransport("SetAVTransportURI",
                ("InstanceID", "0"),
                ("CurrentURI", "http://192.168.1.5/clip.mp4")),
            CancellationToken.None);
        renderer.Position = TimeSpan.FromSeconds(42);
        renderer.Duration = TimeSpan.FromSeconds(90);

        var info = ParseResponse(
            await service.HandleAsync(
                SoapBody.AvTransport("GetPositionInfo", ("InstanceID", "0")),
                CancellationToken.None),
            "GetPositionInfo");

        Assert.Equal("00:00:00", info.Values["RelTime"]);
        Assert.Equal("00:01:30", info.Values["TrackDuration"]);
    }

    [Fact]
    public async Task Seek_accepts_mm_ss_and_updates_the_player()
    {
        var (service, renderer) = Create();
        await service.HandleAsync(
            SoapBody.AvTransport("SetAVTransportURI",
                ("InstanceID", "0"),
                ("CurrentURI", "http://192.168.1.5/clip.mp4")),
            CancellationToken.None);
        renderer.Duration = TimeSpan.FromMinutes(5);

        await service.HandleAsync(
            SoapBody.AvTransport("Seek", ("InstanceID", "0"), ("Unit", "REL_TIME"), ("Target", "00:01:20")),
            CancellationToken.None);

        Assert.Equal(80, renderer.Position.TotalSeconds);
        Assert.Contains("seek:80", renderer.Calls);
    }

    [Fact]
    public async Task Seek_rejects_bad_units_targets_and_positions_past_the_end()
    {
        var (service, renderer) = Create();
        await service.HandleAsync(
            SoapBody.AvTransport("SetAVTransportURI",
                ("InstanceID", "0"),
                ("CurrentURI", "http://192.168.1.5/clip.mp4")),
            CancellationToken.None);
        renderer.Duration = TimeSpan.FromSeconds(30);

        var badUnit = await FaultAsync(() => service.HandleAsync(
            SoapBody.AvTransport("Seek", ("InstanceID", "0"), ("Unit", "TRACK_NR"), ("Target", "1")),
            CancellationToken.None));
        Assert.Equal(UpnpErrorCode.ArgumentValueInvalid, badUnit.Code);

        var badTarget = await FaultAsync(() => service.HandleAsync(
            SoapBody.AvTransport("Seek", ("InstanceID", "0"), ("Unit", "REL_TIME"), ("Target", "not-a-time")),
            CancellationToken.None));
        Assert.Equal(UpnpErrorCode.ArgumentValueInvalid, badTarget.Code);

        var tooFar = await FaultAsync(() => service.HandleAsync(
            SoapBody.AvTransport("Seek", ("InstanceID", "0"), ("Unit", "REL_TIME"), ("Target", "00:10:00")),
            CancellationToken.None));
        Assert.Equal(UpnpErrorCode.ArgumentValueOutOfRange, tooFar.Code);
    }

    [Fact]
    public async Task Stop_and_pause_follow_the_player_and_are_idempotent()
    {
        var (service, renderer) = Create();
        await service.HandleAsync(
            SoapBody.AvTransport("SetAVTransportURI",
                ("InstanceID", "0"),
                ("CurrentURI", "http://192.168.1.5/clip.mp4")),
            CancellationToken.None);

        await service.HandleAsync(SoapBody.AvTransport("Play", ("InstanceID", "0")), CancellationToken.None);
        await service.HandleAsync(SoapBody.AvTransport("Pause", ("InstanceID", "0")), CancellationToken.None);
        await service.HandleAsync(SoapBody.AvTransport("Pause", ("InstanceID", "0")), CancellationToken.None);
        await service.HandleAsync(SoapBody.AvTransport("Stop", ("InstanceID", "0")), CancellationToken.None);
        await service.HandleAsync(SoapBody.AvTransport("Stop", ("InstanceID", "0")), CancellationToken.None);

        Assert.Equal(
            ["load:http://192.168.1.5/clip.mp4", "play", "pause", "pause", "stop", "stop"],
            renderer.Calls);
        Assert.Equal(PlaybackState.Stopped, renderer.State);
    }

    [Fact]
    public async Task Accepts_the_supported_instance_id()
    {
        var (service, _) = Create();
        var response = await service.HandleAsync(
            SoapBody.AvTransport("GetTransportInfo", ("InstanceID", "0")),
            CancellationToken.None);

        Assert.Contains("TransportState", response, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("42")]
    public async Task Unknown_instance_id_faults_with_718(string instanceId)
    {
        var (service, _) = Create();
        var error = await FaultAsync(() => service.HandleAsync(
            SoapBody.AvTransport("GetTransportInfo", ("InstanceID", instanceId)),
            CancellationToken.None));

        Assert.Equal(UpnpErrorCode.InvalidInstanceId, error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    public async Task Invalid_instance_id_faults_with_402(string instanceId)
    {
        var (service, _) = Create();
        var error = await FaultAsync(() => service.HandleAsync(
            SoapBody.AvTransport("GetTransportInfo", ("InstanceID", instanceId)),
            CancellationToken.None));

        Assert.Equal(UpnpErrorCode.InvalidArgs, error.Code);
    }

    [Fact]
    public async Task Unknown_action_faults_with_401()
    {
        var (service, _) = Create();
        var error = await FaultAsync(() => service.HandleAsync(
            SoapBody.AvTransport("SetNextAVTransportURI", ("InstanceID", "0")),
            CancellationToken.None));

        Assert.Equal(UpnpErrorCode.InvalidAction, error.Code);
    }

    [Fact]
    public async Task GetMediaInfo_reflects_the_current_media()
    {
        var (service, renderer) = Create();
        await service.HandleAsync(
            SoapBody.AvTransport("SetAVTransportURI",
                ("InstanceID", "0"),
                ("CurrentURI", "http://192.168.1.5/clip.mp4")),
            CancellationToken.None);
        renderer.Duration = TimeSpan.FromMinutes(2);

        var info = ParseResponse(
            await service.HandleAsync(
                SoapBody.AvTransport("GetMediaInfo", ("InstanceID", "0")),
                CancellationToken.None),
            "GetMediaInfo");

        Assert.Equal("1", info.Values["NrTracks"]);
        Assert.Equal("00:02:00", info.Values["MediaDuration"]);
        Assert.Equal("http://192.168.1.5/clip.mp4", info.Values["CurrentURI"]);
        Assert.Equal("NETWORK", info.Values["PlayMedium"]);
        Assert.Equal(string.Empty, info.Values["NextURI"]);
    }

    [Fact]
    public async Task Soap_parser_rejects_external_entities()
    {
        const string payload =
            "<!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\">]>"
            + "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>"
            + "<u:SetAVTransportURI xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">"
            + "<InstanceID>0</InstanceID><CurrentURI>&xxe;</CurrentURI>"
            + "</u:SetAVTransportURI></s:Body></s:Envelope>";

        var error = await FaultAsync(() => Task.FromResult(SoapXml.Parse(payload, null)));
        Assert.Equal(UpnpErrorCode.InvalidArgs, error.Code);
    }

    [Fact]
    public void Soap_parser_rejects_a_mismatched_soapaction_header()
    {
        var body = SoapBody.Build(
            SoapNamespaces.AvTransportServiceType,
            "Play",
            ("InstanceID", "0"));

        var error = Assert.Throws<UpnpException>(() => SoapXml.Parse(
            body,
            $"\"{SoapNamespaces.AvTransportServiceType}#Pause\""));

        Assert.Equal(UpnpErrorCode.InvalidArgs, error.Code);
    }

    [Fact]
    public void Malformed_soap_is_rejected()
    {
        var error = Assert.Throws<UpnpException>(() => SoapXml.Parse("<not-an-envelope/>", null));
        Assert.Equal(UpnpErrorCode.InvalidArgs, error.Code);
    }

    [Theory]
    [InlineData("00:01:05", 65)]
    [InlineData("0:00:05", 5)]
    [InlineData("01:00:00", 3600)]
    [InlineData("00:00:05.500", 5.5)]
    public void Time_parsing_accepts_protocol_formats(string text, double seconds)
    {
        Assert.True(AvTransportService.TryParseTime(text, out var parsed));
        Assert.Equal(seconds, parsed.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("00:75:00")]
    [InlineData("00:00:75")]
    [InlineData("-00:01:00")]
    [InlineData("a:b:c")]
    public void Time_parsing_rejects_invalid_values(string text)
        => Assert.False(AvTransportService.TryParseTime(text, out _));

    [Fact]
    public void Time_formatting_matches_h_mm_ss()
    {
        Assert.Equal("00:00:00", AvTransportService.FormatTime(TimeSpan.Zero));
        Assert.Equal("01:02:03", AvTransportService.FormatTime(new TimeSpan(1, 2, 3)));
        Assert.Equal("12:00:00", AvTransportService.FormatTime(TimeSpan.FromHours(12)));
    }

    [Theory]
    [InlineData("http://host/a.mp4", true)]
    [InlineData("https://host/a.mp4", true)]
    [InlineData("file:///C:/media/a.mp4", false)]
    [InlineData("rtsp://host/a", false)]
    [InlineData("lavfi://testsrc", false)]
    [InlineData("C:\\media\\a.mp4", false)]
    [InlineData("//host/share/a.mp4", false)]
    public void Media_uri_validation_is_scheme_allowlisted(string text, bool expected)
        => Assert.Equal(expected, AvTransportService.TryCreateMediaUri(text, out _));
}
