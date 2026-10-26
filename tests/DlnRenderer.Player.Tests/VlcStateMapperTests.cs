using DlnRenderer.Core.Models;
using DlnRenderer.Player.Vlc;

namespace DlnRenderer.Player.Tests;

/// <summary>
/// libvlc signal → domain state mapping. Mirrors <see cref="MpvStateMapperTests"/>: the mapper is
/// pure, so every combination the event handlers can produce is pinned here without loading libvlc.
/// </summary>
public class VlcStateMapperTests
{
    private static VlcTransportSnapshot Snapshot(
        bool hasError = false,
        bool reachedEnd = false,
        bool isStopped = false,
        bool isOpening = false,
        bool isBuffering = false,
        bool isPaused = false,
        bool isPlaying = false)
        => new(hasError, reachedEnd, isStopped, isOpening, isBuffering, isPaused, isPlaying);

    [Fact]
    public void Fresh_player_maps_to_stopped()
    {
        Assert.Equal(PlaybackState.Stopped, VlcStateMapper.Map(Snapshot()));
    }

    [Fact]
    public void Nothing_special_maps_to_stopped()
    {
        Assert.Equal(PlaybackState.Stopped, VlcStateMapper.Map(Snapshot(isStopped: true)));
    }

    [Fact]
    public void Opening_medium_maps_to_transitioning()
    {
        Assert.Equal(PlaybackState.Transitioning, VlcStateMapper.Map(Snapshot(isOpening: true)));
    }

    [Fact]
    public void Starved_input_maps_to_buffering()
    {
        Assert.Equal(PlaybackState.Buffering, VlcStateMapper.Map(Snapshot(isBuffering: true)));
    }

    [Fact]
    public void Playing_medium_maps_to_playing()
    {
        Assert.Equal(PlaybackState.Playing, VlcStateMapper.Map(Snapshot(isPlaying: true)));
    }

    [Fact]
    public void Paused_medium_maps_to_paused()
    {
        Assert.Equal(PlaybackState.Paused, VlcStateMapper.Map(Snapshot(isPaused: true)));
    }

    [Fact]
    public void Ended_medium_maps_to_stopped()
    {
        Assert.Equal(PlaybackState.Stopped, VlcStateMapper.Map(Snapshot(reachedEnd: true)));
    }

    [Fact]
    public void Error_beats_every_other_signal()
    {
        // libvlc can still report Playing while the error callback has fired; the renderer must
        // never advertise a cast as healthy when the engine already gave up.
        Assert.Equal(PlaybackState.Error, VlcStateMapper.Map(Snapshot(hasError: true, isPlaying: true)));
        Assert.Equal(PlaybackState.Error, VlcStateMapper.Map(Snapshot(hasError: true, reachedEnd: true)));
    }

    [Fact]
    public void End_beats_pause()
    {
        // A medium that reached its end while a pause was requested must read as stopped, or the
        // control point would show a pause button over a dead stream.
        Assert.Equal(PlaybackState.Stopped, VlcStateMapper.Map(Snapshot(reachedEnd: true, isPaused: true)));
    }
}
