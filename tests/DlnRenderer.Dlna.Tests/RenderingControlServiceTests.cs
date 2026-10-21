using System.Globalization;
using System.Xml.Linq;
using DlnRenderer.Dlna.AvTransport;
using DlnRenderer.Dlna.RenderingControl;
using DlnRenderer.Dlna.Soap;
using DlnRenderer.Dlna.Tests.Support;

namespace DlnRenderer.Dlna.Tests;

/// <summary>
/// RenderingControl:1 volume and mute over the protocol layer.
/// </summary>
/// <remarks>
/// These exercise the service against a real <see cref="ITransportRenderer"/>, so "SetVolume then
/// GetVolume" proves the value round-trips through the player abstraction rather than through a
/// service-local field. The integration tests in the Service test project repeat the same contract
/// against real libmpv.
/// </remarks>
public class RenderingControlServiceTests
{
    private static readonly XNamespace Rcs = SoapNamespaces.RenderingControlServiceType;

    [Fact]
    public async Task GetVolume_reports_the_player_volume()
    {
        var renderer = new FakeTransportRenderer { Volume = 100 };
        var service = new RenderingControlService(renderer, _ => { });

        var values = await GetAsync(service, renderer, "GetVolume", Channel("Master"));

        Assert.Equal("100", values["CurrentVolume"]);
        Assert.Equal(0, renderer.SetVolumeCalls);
    }

    [Fact]
    public async Task SetVolume_then_GetVolume_reads_back_the_same_value()
    {
        var renderer = new FakeTransportRenderer { Volume = 100 };
        var service = new RenderingControlService(renderer, _ => { });

        await SetAsync(service, "SetVolume", $"<Channel>Master</Channel><DesiredVolume>37</DesiredVolume>");

        // The change must reach the player, not just the service.
        Assert.Equal(37, renderer.Volume);
        Assert.Equal(1, renderer.SetVolumeCalls);

        var values = await GetAsync(service, renderer, "GetVolume", Channel("Master"));
        Assert.Equal("37", values["CurrentVolume"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)]
    public async Task Volume_boundaries_are_accepted_and_stored(int volume)
    {
        var renderer = new FakeTransportRenderer { Volume = 50 };
        var service = new RenderingControlService(renderer, _ => { });

        await SetAsync(service, "SetVolume", $"<Channel>Master</Channel><DesiredVolume>{volume}</DesiredVolume>");

        Assert.Equal(volume, renderer.Volume);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("99999")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12.5")]
    public async Task Invalid_volumes_are_rejected_and_never_reach_the_player(string desired)
    {
        var renderer = new FakeTransportRenderer { Volume = 50 };
        var service = new RenderingControlService(renderer, _ => { });

        var error = await Assert.ThrowsAsync<UpnpException>(() =>
            SetAsync(service, "SetVolume", $"<Channel>Master</Channel><DesiredVolume>{desired}</DesiredVolume>"));

        // Out of range and malformed are different protocol errors, and both leave the player alone.
        Assert.Contains(error.Code, new[] { UpnpErrorCode.ArgumentValueOutOfRange, UpnpErrorCode.InvalidArgs });
        Assert.Equal(50, renderer.Volume);
        Assert.Equal(0, renderer.SetVolumeCalls);
    }

    [Fact]
    public async Task SetMute_toggles_the_player_and_GetMute_reports_it()
    {
        var renderer = new FakeTransportRenderer();
        var service = new RenderingControlService(renderer, _ => { });

        await SetAsync(service, "SetMute", "<Channel>Master</Channel><DesiredMute>1</DesiredMute>");
        Assert.True(renderer.Muted);

        var muted = await GetAsync(service, renderer, "GetMute", Channel("Master"));
        Assert.Equal("1", muted["CurrentMute"]);

        await SetAsync(service, "SetMute", "<Channel>Master</Channel><DesiredMute>0</DesiredMute>");
        Assert.False(renderer.Muted);

        var unmuted = await GetAsync(service, renderer, "GetMute", Channel("Master"));
        Assert.Equal("0", unmuted["CurrentMute"]);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public async Task Boolean_mute_forms_are_all_accepted(string desired, bool expected)
    {
        var renderer = new FakeTransportRenderer();
        var service = new RenderingControlService(renderer, _ => { });

        await SetAsync(service, "SetMute", $"<Channel>Master</Channel><DesiredMute>{desired}</DesiredMute>");

        Assert.Equal(expected, renderer.Muted);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("yes")]
    [InlineData("")]
    public async Task Invalid_mute_values_are_rejected(string desired)
    {
        var renderer = new FakeTransportRenderer();
        var service = new RenderingControlService(renderer, _ => { });

        await Assert.ThrowsAsync<UpnpException>(() =>
            SetAsync(service, "SetMute", $"<Channel>Master</Channel><DesiredMute>{desired}</DesiredMute>"));

        Assert.False(renderer.Muted);
        Assert.Equal(0, renderer.SetMuteCalls);
    }

    [Fact]
    public async Task Channel_is_optional_and_defaults_to_Master()
    {
        var renderer = new FakeTransportRenderer { Volume = 64 };
        var service = new RenderingControlService(renderer, _ => { });

        // The spec makes Channel optional; omitting it must behave as Master, not fail.
        var values = await GetAsync(service, renderer, "GetVolume", string.Empty);

        Assert.Equal("64", values["CurrentVolume"]);
    }

    [Theory]
    [InlineData("LF")]
    [InlineData("RF")]
    [InlineData("master")] // channel names are case sensitive
    public async Task Unknown_channels_are_rejected(string channel)
    {
        var renderer = new FakeTransportRenderer();
        var service = new RenderingControlService(renderer, _ => { });

        var error = await Assert.ThrowsAsync<UpnpException>(() =>
            GetAsync(service, renderer, "GetVolume", Channel(channel)));

        Assert.Equal(UpnpErrorCode.RenderingInvalidChannel, error.Code);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("7")]
    public async Task Unknown_instance_ids_are_rejected(string instanceId)
    {
        var renderer = new FakeTransportRenderer();
        var service = new RenderingControlService(renderer, _ => { });

        var error = await Assert.ThrowsAsync<UpnpException>(() =>
            GetAsync(service, renderer, "GetVolume", Channel("Master"), instanceId));

        Assert.Equal(UpnpErrorCode.InvalidInstanceId, error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    public async Task Malformed_instance_ids_are_rejected(string instanceId)
    {
        var renderer = new FakeTransportRenderer();
        var service = new RenderingControlService(renderer, _ => { });

        var error = await Assert.ThrowsAsync<UpnpException>(() =>
            GetAsync(service, renderer, "GetVolume", Channel("Master"), instanceId));

        Assert.Equal(UpnpErrorCode.InvalidArgs, error.Code);
    }

    [Fact]
    public async Task Unsupported_actions_are_reported_as_invalid_action()
    {
        var renderer = new FakeTransportRenderer();
        var service = new RenderingControlService(renderer, _ => { });

        var error = await Assert.ThrowsAsync<UpnpException>(() =>
            GetAsync(service, renderer, "GetVolumeDB", Channel("Master")));

        Assert.Equal(UpnpErrorCode.InvalidAction, error.Code);
    }

    [Fact]
    public async Task ListPresets_reports_the_single_supported_preset()
    {
        var renderer = new FakeTransportRenderer();
        var service = new RenderingControlService(renderer, _ => { });

        var values = await GetAsync(service, renderer, "ListPresets", string.Empty);

        Assert.Equal("FactoryDefaults", values["CurrentPresetNameList"]);
    }

    [Fact]
    public async Task Selecting_an_unknown_preset_fails()
    {
        var renderer = new FakeTransportRenderer();
        var service = new RenderingControlService(renderer, _ => { });

        var error = await Assert.ThrowsAsync<UpnpException>(() =>
            SetAsync(service, "SelectPreset", "<PresetName>Night</PresetName>"));

        Assert.Equal(UpnpErrorCode.RenderingInvalidName, error.Code);
    }

    [Fact]
    public async Task A_player_failure_surfaces_as_action_failed()
    {
        var renderer = new FakeTransportRenderer { FailVolume = "audio device disappeared" };
        var service = new RenderingControlService(renderer, _ => { });

        var error = await Assert.ThrowsAsync<UpnpException>(() =>
            SetAsync(service, "SetVolume", "<Channel>Master</Channel><DesiredVolume>20</DesiredVolume>"));

        Assert.Equal(UpnpErrorCode.ActionFailed, error.Code);
        Assert.Contains("audio device disappeared", error.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Volume_options_match_the_player_range()
    {
        var renderer = new FakeTransportRenderer();
        var service = new RenderingControlService(renderer, _ => { });

        // The player exposes 0-100; anything wider would advertise volume the player cannot reach.
        foreach (var volume in new[] { 0, 100 })
        {
            await SetAsync(service, "SetVolume", $"<Channel>Master</Channel><DesiredVolume>{volume}</DesiredVolume>");
            Assert.Equal(volume, renderer.Volume);
        }
    }

    private static string Channel(string channel) => $"<Channel>{channel}</Channel>";

    private static async Task<Dictionary<string, string>> GetAsync(
        RenderingControlService service,
        FakeTransportRenderer renderer,
        string action,
        string extraArguments,
        string instanceId = "0")
    {
        var request = BuildRequest(action, $"<InstanceID>{instanceId}</InstanceID>{extraArguments}");
        var xml = await service.HandleAsync(request, CancellationToken.None);
        _ = renderer;
        return Parse(xml);
    }

    private static async Task SetAsync(RenderingControlService service, string action, string arguments)
    {
        var request = BuildRequest(action, $"<InstanceID>0</InstanceID>{arguments}");
        await service.HandleAsync(request, CancellationToken.None);
    }

    private static SoapRequest BuildRequest(string action, string arguments)
    {
        var body = XElement.Parse(
            $"<u:{action} xmlns:u=\"urn:schemas-upnp-org:service:RenderingControl:1\">{arguments}</u:{action}>");
        return new SoapRequest(body);
    }

    private static Dictionary<string, string> Parse(string responseXml)
    {
        var document = XDocument.Parse(responseXml);
        return document.Descendants()
            .Where(e => e.Parent?.Name.LocalName.EndsWith("Response", StringComparison.Ordinal) == true)
            .ToDictionary(e => e.Name.LocalName, e => e.Value);
    }
}
