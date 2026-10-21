using DlnRenderer.Core.Models;
using DlnRenderer.Player.Mpv;

namespace DlnRenderer.Player.Tests;

/// <summary>
/// Ordering and visibility of player commands.
/// </summary>
/// <remarks>
/// These are integration tests (they need libmpv) because the behaviour being asserted is libmpv's
/// asynchronicity: a command returns before the player reports the new value, and two unsynchronised
/// commands can land in either order. Both defects were observed on the real player, so a mock would
/// not reproduce them.
/// </remarks>
public class MpvCommandOrderingTests
{
    [MpvFact]
    public async Task Volume_is_observable_immediately_after_the_setter_returns()
    {
        await using var player = CreatePlayer();

        await player.SetVolumeAsync(37, CancellationToken.None);

        // The previous implementation returned after issuing the command, so this read still saw
        // the old value whenever playback was active.
        var status = await player.GetStatusAsync(CancellationToken.None);
        Assert.Equal(37, status.Volume);
    }

    [MpvFact]
    public async Task Volume_is_observable_during_playback_too()
    {
        await using var player = CreatePlayer();
        await player.LoadAsync(new MediaInfo(new Uri(TestMedia.VideoFixturePath)), CancellationToken.None);
        await player.PlayAsync(CancellationToken.None);

        await player.SetVolumeAsync(66, CancellationToken.None);

        Assert.Equal(66, (await player.GetStatusAsync(CancellationToken.None)).Volume);
    }

    [MpvFact]
    public async Task Mute_is_observable_immediately_after_the_setter_returns()
    {
        await using var player = CreatePlayer();

        await player.SetMuteAsync(true, CancellationToken.None);
        Assert.True((await player.GetStatusAsync(CancellationToken.None)).Muted);

        await player.SetMuteAsync(false, CancellationToken.None);
        Assert.False((await player.GetStatusAsync(CancellationToken.None)).Muted);
    }

    [MpvFact]
    public async Task Sequential_volume_writes_end_on_the_last_value()
    {
        await using var player = CreatePlayer();

        foreach (var volume in new[] { 10, 20, 30, 40, 50 })
        {
            await player.SetVolumeAsync(volume, CancellationToken.None);
            Assert.Equal(volume, (await player.GetStatusAsync(CancellationToken.None)).Volume);
        }
    }

    [MpvFact]
    public async Task Volume_outside_the_player_range_is_rejected()
    {
        await using var player = CreatePlayer();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => player.SetVolumeAsync(-1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => player.SetVolumeAsync(101, CancellationToken.None));
    }

    private static MpvPlayer CreatePlayer() => new(new MpvPlayerOptions
    {
        VideoOutput = "null",
        AudioOutput = "null",
    });
}
