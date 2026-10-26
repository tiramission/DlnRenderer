using System.Text.Json;
using DlnRenderer.Core.Ipc;
using DlnRenderer.Core.Models;
using DlnRenderer.Core.Settings;

namespace DlnRenderer.Core.Tests;

/// <summary>
/// The dual-engine plumbing that must hold without any player loaded: the settings field validates,
/// the engine ids survive a settings file round trip, and the IPC snapshot carries the kernel name
/// without breaking a service that predates the field.
/// </summary>
public sealed class PlaybackEngineTests
{
    [Fact]
    public void Default_engine_is_mpv()
    {
        Assert.Equal(PlaybackEngine.Mpv, RendererSettings.Default.PlaybackEngine);
    }

    [Fact]
    public void Both_engines_validate()
    {
        foreach (var engine in new[] { PlaybackEngine.Mpv, PlaybackEngine.Vlc })
        {
            var settings = new RendererSettings { DeviceName = "测试", PlaybackEngine = engine };
            Assert.True(RendererSettings.TryValidate(settings, out var error), error);
        }
    }

    [Fact]
    public void Out_of_range_engine_is_rejected()
    {
        // A hand-edited settings file can carry any integer; validation is what stops the service
        // from silently starting on whatever the cast lands on.
        var settings = new RendererSettings { DeviceName = "测试", PlaybackEngine = (PlaybackEngine)42 };
        Assert.False(RendererSettings.TryValidate(settings, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Engine_survives_settings_file_round_trip()
    {
        var settings = new RendererSettings { DeviceName = "测试", PlaybackEngine = PlaybackEngine.Vlc };
        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<RendererSettings>(json);

        Assert.Equal(PlaybackEngine.Vlc, restored!.PlaybackEngine);
    }

    [Fact]
    public void Engine_ids_round_trip()
    {
        Assert.Equal("mpv", PlaybackEngineInfo.Id(PlaybackEngine.Mpv));
        Assert.Equal("vlc", PlaybackEngineInfo.Id(PlaybackEngine.Vlc));
        Assert.Equal(PlaybackEngine.Mpv, PlaybackEngineInfo.FromId("mpv"));
        Assert.Equal(PlaybackEngine.Vlc, PlaybackEngineInfo.FromId("vlc"));

        // Unknown or missing ids fall back to mpv rather than throwing: an old UI naming a new
        // engine must degrade to a readable default, not crash the label.
        Assert.Equal(PlaybackEngine.Mpv, PlaybackEngineInfo.FromId(null));
        Assert.Equal(PlaybackEngine.Mpv, PlaybackEngineInfo.FromId("kodi"));
    }

    [Fact]
    public void Snapshot_engine_is_optional_on_the_wire()
    {
        // A service built before the field sends no Engine at all; the message must still parse.
        var old = JsonSerializer.Deserialize<RendererSnapshot>(
            """{"playback":{"state":"stopped"},"deviceName":"DlnRenderer"}""",
            IpcJson.Options);
        Assert.Null(old!.Engine);

        var snapshot = new RendererSnapshot
        {
            DeviceName = "DlnRenderer",
            Engine = PlaybackEngineInfo.Id(PlaybackEngine.Vlc),
        };
        var roundTrip = JsonSerializer.Deserialize<RendererSnapshot>(
            JsonSerializer.Serialize(snapshot, IpcJson.Options), IpcJson.Options);

        Assert.Equal("vlc", roundTrip!.Engine);
    }
}
