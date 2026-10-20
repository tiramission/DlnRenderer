using DlnRenderer.Core.Models;
using DlnRenderer.Player.Mpv;

namespace DlnRenderer.Player.Tests;

public class MpvStateMapperTests
{
    [Fact]
    public void Idle_maps_to_stopped()
    {
        Assert.Equal(
            PlaybackState.Stopped,
            MpvStateMapper.Map(new MpvTransportSnapshot(IsIdle: true, IsPaused: false, IsBuffering: false, HasError: false)));
    }

    [Fact]
    public void Paused_file_maps_to_paused()
    {
        Assert.Equal(
            PlaybackState.Paused,
            MpvStateMapper.Map(new MpvTransportSnapshot(IsIdle: false, IsPaused: true, IsBuffering: false, HasError: false)));
    }

    [Fact]
    public void Running_file_maps_to_playing()
    {
        Assert.Equal(
            PlaybackState.Playing,
            MpvStateMapper.Map(new MpvTransportSnapshot(IsIdle: false, IsPaused: false, IsBuffering: false, HasError: false)));
    }

    [Fact]
    public void Starved_decoder_maps_to_buffering()
    {
        Assert.Equal(
            PlaybackState.Buffering,
            MpvStateMapper.Map(new MpvTransportSnapshot(IsIdle: false, IsPaused: false, IsBuffering: true, HasError: false)));
    }

    [Fact]
    public void Error_wins_over_every_other_signal()
    {
        Assert.Equal(
            PlaybackState.Error,
            MpvStateMapper.Map(new MpvTransportSnapshot(IsIdle: true, IsPaused: true, IsBuffering: true, HasError: true)));
    }

    [Fact]
    public void Every_mapped_state_is_a_legal_transition_from_transitioning()
    {
        // Media loads always enter Transitioning first, so every mapped state must be reachable
        // from there. Stopped -> Paused is intentionally not legal, which is why the player
        // marks loads as pending instead of mapping the stale idle flag.
        var inputs = new[]
        {
            new MpvTransportSnapshot(true, false, false, false),
            new MpvTransportSnapshot(false, true, false, false),
            new MpvTransportSnapshot(false, false, false, false),
            new MpvTransportSnapshot(false, false, true, false),
            new MpvTransportSnapshot(false, false, false, true),
        };

        foreach (var input in inputs)
        {
            var to = MpvStateMapper.Map(input);
            var legal = DlnRenderer.Core.State.PlaybackStateRules.CanTransition(
                PlaybackState.Transitioning, to);
            Assert.True(legal, $"illegal transition Transitioning -> {to}");
        }
    }
}
