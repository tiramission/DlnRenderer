using DlnRenderer.Core.Models;
using DlnRenderer.Player.Mpv;

namespace DlnRenderer.Player.Tests;

/// <summary>
/// Reloading a medium after it was stopped, at the level of libmpv alone.
/// </summary>
/// <remarks>
/// This is the state machine behind every "cast the same thing again" flow: a control point stops,
/// then asks for the same media. libmpv clears <c>pause</c> while opening a file, so a load issued as
/// "open paused" lands playing - and a renderer that waits for the player to come up then waits
/// forever, because the player neither plays nor reports paused. The reload path used to time out
/// after 3 × 5 s and took progress control with it.
/// </remarks>
public class MpvPlayerReloadTests
{
    [MpvFact]
    public async Task Reload_after_stop_reaches_paused()
    {
        await using var player = new MpvPlayer(new MpvPlayerOptions { AudioOutput = "null" });
        var media = new MediaInfo(new Uri(TestMedia.VideoFixturePath));

        await player.LoadAsync(media, CancellationToken.None);
        Assert.True(await WaitAsync(player, s => s.State == PlaybackState.Paused, 10), "first load did not reach Paused");

        await player.StopAsync(CancellationToken.None);
        Assert.True(await WaitAsync(player, s => s.State == PlaybackState.Stopped, 5), "stop did not reach Stopped");

        await player.LoadAsync(media, CancellationToken.None);
        Assert.True(
            await WaitAsync(player, s => s.State == PlaybackState.Paused, 6),
            $"reload after stop did not reach Paused; state was {(await player.GetStatusAsync(CancellationToken.None)).State}");
    }

    [MpvFact]
    public async Task Play_issued_before_the_file_is_loaded_wins_over_the_load_pause()
    {
        await using var player = new MpvPlayer(new MpvPlayerOptions { AudioOutput = "null" });
        var media = new MediaInfo(new Uri(TestMedia.VideoFixturePath));

        // Casting clients send Play immediately after SetAVTransportURI; re-applying the load's pause
        // intent when the file finally opens would silently undo that Play.
        await player.LoadAsync(media, CancellationToken.None);
        await player.PlayAsync(CancellationToken.None);

        Assert.True(
            await WaitAsync(player, s => s.State == PlaybackState.Playing, 10),
            "Play did not take effect");
    }

    private static async Task<bool> WaitAsync(MpvPlayer player, Func<PlaybackStatus, bool> predicate, int seconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate(await player.GetStatusAsync(CancellationToken.None)))
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }
}
