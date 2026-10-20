using System.Diagnostics;
using DlnRenderer.Core.Models;
using DlnRenderer.Player.Mpv;

namespace DlnRenderer.Player.Tests;

/// <summary>
/// Exercises the real libmpv engine. Output devices are set to <c>null</c> so these tests are
/// deterministic on machines without a sound card; the actual device output is verified
/// interactively through the app (see docs/testing.md).
/// </summary>
public sealed class MpvPlayerIntegrationTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), "dlnrenderer-tests", Guid.NewGuid().ToString("N"));

    private static MpvPlayer CreatePlayer() => new(new MpvPlayerOptions
    {
        AudioOutput = "null",
        VideoOutput = "null",
        PositionUpdateInterval = TimeSpan.FromMilliseconds(100),
    });

    [MpvFact]
    public async Task Audio_playback_pause_seek_volume_and_stop_work()
    {
        var wav = TestMedia.CreateToneWav(_workDirectory, seconds: 6);
        var statusUpdates = 0;

        await using var player = CreatePlayer();
        player.StatusChanged += (_, _) => Interlocked.Increment(ref statusUpdates);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        await player.LoadAsync(new MediaInfo(new Uri(wav)) { Title = "tone" }, timeout.Token);

        var loaded = await WaitForAsync(player, s => s.State == PlaybackState.Paused, "paused after load");
        Assert.Equal(MediaKind.Audio, loaded.MediaKind);
        Assert.NotNull(loaded.Duration);
        Assert.InRange(loaded.Duration!.Value.TotalSeconds, 5.5, 6.5);

        await player.PlayAsync(timeout.Token);
        var playing = await WaitForAsync(
            player,
            s => s.State == PlaybackState.Playing && s.Position > TimeSpan.FromMilliseconds(300),
            "playing with advancing position");
        Assert.Equal("tone", playing.Media?.Title);
        var updatesBeforePause = Volatile.Read(ref statusUpdates);
        Assert.True(updatesBeforePause > 1, "expected more than one status update while playing");

        await player.PauseAsync(timeout.Token);
        await WaitForAsync(player, s => s.State == PlaybackState.Paused, "paused after play");
        var pausedPosition = (await player.GetStatusAsync(timeout.Token)).Position;
        await Task.Delay(700, timeout.Token);
        var pausedPositionAfter = (await player.GetStatusAsync(timeout.Token)).Position;
        Assert.True(
            pausedPositionAfter - pausedPosition < TimeSpan.FromMilliseconds(300),
            $"position advanced while paused: {pausedPosition} -> {pausedPositionAfter}");

        await player.SeekAsync(TimeSpan.FromSeconds(4), timeout.Token);
        await WaitForAsync(
            player,
            s => s.Position >= TimeSpan.FromSeconds(3.8) && s.Position < TimeSpan.FromSeconds(5.5),
            "seek to 4s");

        await player.SetVolumeAsync(35, timeout.Token);
        await WaitForAsync(player, s => s.Volume == 35, "volume 35");

        await player.SetMuteAsync(true, timeout.Token);
        await WaitForAsync(player, s => s.Muted, "muted");

        await player.StopAsync(timeout.Token);
        var stopped = await WaitForAsync(player, s => s.State == PlaybackState.Stopped, "stopped");
        Assert.Equal(TimeSpan.Zero, stopped.Position);
    }

    [MpvFact]
    public async Task Video_file_is_decoded_and_reports_video_kind()
    {
        Assert.True(File.Exists(TestMedia.VideoFixturePath), $"missing fixture: {TestMedia.VideoFixturePath}");

        await using var player = CreatePlayer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        await player.LoadAsync(new MediaInfo(new Uri(TestMedia.VideoFixturePath)), timeout.Token);
        var loaded = await WaitForAsync(player, s => s.State == PlaybackState.Paused, "paused after load");

        Assert.Equal(MediaKind.Video, loaded.MediaKind);
        Assert.InRange(loaded.Duration!.Value.TotalSeconds, 1.5, 2.5);

        await player.PlayAsync(timeout.Token);
        await WaitForAsync(
            player,
            s => s.State == PlaybackState.Playing && s.Position > TimeSpan.FromMilliseconds(300),
            "video playback advancing");
    }

    [MpvFact]
    public async Task Unplayable_file_surfaces_an_error_state()
    {
        var missing = Path.Combine(_workDirectory, "does-not-exist.mp4");

        await using var player = CreatePlayer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        await player.LoadAsync(new MediaInfo(new Uri(missing)), timeout.Token);

        var failed = await WaitForAsync(player, s => s.State == PlaybackState.Error, "error state");
        Assert.False(string.IsNullOrWhiteSpace(failed.ErrorMessage));
    }

    [MpvFact]
    public async Task Dispose_releases_native_resources_quickly_and_is_idempotent()
    {
        var player = CreatePlayer();
        var stopwatch = Stopwatch.StartNew();

        await player.DisposeAsync();
        await player.DisposeAsync();

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"dispose took {stopwatch.Elapsed}");

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => player.GetStatusAsync(CancellationToken.None));
    }

    [MpvFact]
    public async Task Sequential_loads_replace_media_and_reset_position()
    {
        var first = TestMedia.CreateToneWav(_workDirectory, seconds: 5);
        var third = TestMedia.CreateToneWav(_workDirectory, seconds: 5);
        var video = TestMedia.VideoFixturePath;

        await using var player = CreatePlayer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await player.LoadAsync(new MediaInfo(new Uri(first)) { Title = "first" }, timeout.Token);
        var loaded = await WaitForAsync(player, s => s.State == PlaybackState.Paused && s.Media?.Title == "first", "first paused");
        Assert.Equal(MediaKind.Audio, loaded.MediaKind);

        await player.PlayAsync(timeout.Token);
        await WaitForAsync(player, s => s.State == PlaybackState.Playing && s.Position > TimeSpan.FromMilliseconds(300), "first playing");

        // Replace while playing.
        await player.LoadAsync(new MediaInfo(new Uri(video)) { Title = "second" }, timeout.Token);
        var second = await WaitForAsync(player, s => s.State == PlaybackState.Paused && s.Media?.Title == "second", "second paused");
        Assert.Equal(MediaKind.Video, second.MediaKind);
        Assert.True(second.Position < TimeSpan.FromSeconds(1), $"position was not reset: {second.Position}");

        await player.PlayAsync(timeout.Token);
        await WaitForAsync(player, s => s.State == PlaybackState.Playing && s.Position > TimeSpan.FromMilliseconds(200), "second playing");

        await player.LoadAsync(new MediaInfo(new Uri(third)) { Title = "third" }, timeout.Token);
        var thirdStatus = await WaitForAsync(player, s => s.State == PlaybackState.Paused && s.Media?.Title == "third", "third paused");
        Assert.Equal(MediaKind.Audio, thirdStatus.MediaKind);

        await player.StopAsync(timeout.Token);
        await WaitForAsync(player, s => s.State == PlaybackState.Stopped, "stopped after third");
    }

    [MpvFact]
    public async Task Dispose_while_playing_does_not_hang()
    {
        var wav = TestMedia.CreateToneWav(_workDirectory, seconds: 20);

        var player = CreatePlayer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        await player.LoadAsync(new MediaInfo(new Uri(wav)), timeout.Token);
        await WaitForAsync(player, s => s.State == PlaybackState.Paused, "paused");
        await player.PlayAsync(timeout.Token);
        await WaitForAsync(player, s => s.State == PlaybackState.Playing && s.Position > TimeSpan.FromMilliseconds(200), "playing");

        var stopwatch = Stopwatch.StartNew();
        await player.DisposeAsync();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"dispose took {stopwatch.Elapsed}");
    }

    private static async Task<PlaybackStatus> WaitForAsync(
        MpvPlayer player,
        Func<PlaybackStatus, bool> predicate,
        string description)
    {
        var stopwatch = Stopwatch.StartNew();
        PlaybackStatus last = PlaybackStatus.Empty;

        while (stopwatch.Elapsed < DefaultTimeout)
        {
            last = await player.GetStatusAsync(CancellationToken.None);
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException(
            $"Timed out after {DefaultTimeout} waiting for '{description}'. Last status: " +
            $"state={last.State}, kind={last.MediaKind}, position={last.Position}, duration={last.Duration}, " +
            $"volume={last.Volume}, muted={last.Muted}, error={last.ErrorMessage ?? "<none>"}");
    }
}
