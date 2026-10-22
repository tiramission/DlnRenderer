using System.Diagnostics;
using System.Runtime.InteropServices;
using DlnRenderer.Core.Models;
using DlnRenderer.Dlna;
using DlnRenderer.Player.Mpv;
using DlnRenderer.Service.Hosting;

namespace DlnRenderer.Service.Tests;

/// <summary>
/// The presentation window driven by the real player, end to end inside the test process.
/// </summary>
/// <remarks>
/// This covers the layer the window tests cannot reach: they drive <see cref="VideoPresenter"/>
/// directly, while this drives <see cref="RendererSession"/>, which decides *when* the overlay is
/// shown. Load-versus-play, pause-versus-stop and media switches are exactly where a regression
/// hides the overlay too early or leaves a blank fullscreen window behind.
/// <para>
/// Requires <c>libmpv-2.dll</c> and <c>ffmpeg</c>; skipped otherwise, like the player's own
/// integration tests.
/// </para>
/// </remarks>
[Collection("video-window")]
public class SessionWindowLifecycleTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _work = Path.Combine(
        Path.GetTempPath(),
        "dlnrenderer-window-tests",
        Guid.NewGuid().ToString("N"));

    public SessionWindowLifecycleTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // A failed delete of a scratch directory must not mask a test result.
        }
    }

    [MpvFFmpegFact]
    public async Task Loading_media_does_not_take_the_screen_and_playing_does()
    {
        var media = CreateVideo();

        await using var ctx = await SessionContext.CreateAsync();

        await ctx.Session.LoadAsync(new Uri(media), Didl(media), CancellationToken.None);

        // `LoadAsync` returns once `loadfile` was accepted; the window decision lands a moment later
        // when the player reports the file. Wait for that report, not a fixed sleep.
        await WaitUntil(
            () => ctx.Session.GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult().State == PlaybackState.Paused,
            "media loaded and paused");
        await Task.Delay(500);

        Assert.Equal("hidden", ctx.Presenter.PresentationState);
        Assert.False(ctx.Presenter.IsVisible);

        await ctx.Session.PlayAsync(CancellationToken.None);

        // Visibility is published before the topmost style and the client size are applied on the
        // window thread, so wait for each condition instead of assuming one delay covers them all.
        await WaitUntil(() => ctx.Presenter.IsVisible, "visible after play");
        await WaitUntil(() => ctx.Presenter.IsTopmost, "topmost after play");
        await WaitUntil(
            () => ctx.Presenter.ClientWidth > 100 && ctx.Presenter.ClientHeight > 100,
            "overlay sized to the monitor");

        Assert.Equal("fullscreen", ctx.Presenter.PresentationState);
    }

    [MpvFFmpegFact]
    public async Task Pause_keeps_the_overlay_and_stop_takes_it_down()
    {
        var media = CreateVideo();

        await using var ctx = await SessionContext.CreateAsync();
        await ctx.Session.LoadAsync(new Uri(media), Didl(media), CancellationToken.None);
        await ctx.Session.PlayAsync(CancellationToken.None);
        await WaitUntil(() => ctx.Presenter.IsVisible, "visible after play");
        var window = ctx.Presenter.WindowHandle;

        await ctx.Session.PauseAsync(CancellationToken.None);
        await Task.Delay(500);

        // A paused cast is still a cast; dropping the overlay looks like the cast was dropped.
        Assert.True(ctx.Presenter.IsVisible, "pause must not dismiss the overlay");
        Assert.Equal(window, ctx.Presenter.WindowHandle);

        await ctx.Session.StopAsync(CancellationToken.None);
        await WaitUntil(() => !ctx.Presenter.IsVisible, "hidden after stop");

        // Hidden, not destroyed: mpv's wid is fixed, so the next Play reuses this window.
        Assert.Equal(window, ctx.Presenter.WindowHandle);
        Assert.True(IsWindow(window));
    }

    [MpvFFmpegFact]
    public async Task Playing_to_the_end_hides_the_overlay()
    {
        var media = CreateVideo(seconds: 2);

        await using var ctx = await SessionContext.CreateAsync();
        await ctx.Session.LoadAsync(new Uri(media), Didl(media), CancellationToken.None);
        await ctx.Session.PlayAsync(CancellationToken.None);
        await WaitUntil(() => ctx.Presenter.IsVisible, "visible after play");

        // libmpv runs with keep-open=no, so the end of the file surfaces as Stopped - the same
        // terminal state as Stop - and the overlay must follow it down.
        await WaitUntil(() => !ctx.Presenter.IsVisible, "hidden after end of media");
    }

    [MpvFFmpegFact]
    public async Task Switching_media_reuses_the_same_window()
    {
        var first = CreateVideo();
        var second = CreateVideo();

        await using var ctx = await SessionContext.CreateAsync();
        await ctx.Session.LoadAsync(new Uri(first), Didl(first), CancellationToken.None);
        await ctx.Session.PlayAsync(CancellationToken.None);
        await WaitUntil(() => ctx.Presenter.IsVisible, "visible after first play");
        var window = ctx.Presenter.WindowHandle;
        var child = ctx.Presenter.ChildHandle;

        await ctx.Session.LoadAsync(new Uri(second), Didl(second), CancellationToken.None);
        await ctx.Session.PlayAsync(CancellationToken.None);
        await WaitUntil(() => ctx.Presenter.IsVisible, "visible after second play");

        // mpv's wid is fixed at initialisation, so a different HWND would mean a second player.
        Assert.Equal(window, ctx.Presenter.WindowHandle);
        Assert.Equal(child, ctx.Presenter.ChildHandle);
    }

    [MpvFFmpegFact]
    public async Task Repeated_play_and_stop_keep_the_window_consistent()
    {
        var media = CreateVideo();

        await using var ctx = await SessionContext.CreateAsync();
        await ctx.Session.LoadAsync(new Uri(media), Didl(media), CancellationToken.None);

        for (var i = 0; i < 3; i++)
        {
            // PlayAsync returns while the player is still starting, so wait for a defined outcome
            // instead of racing the overlay.
            await ctx.Session.PlayAsync(CancellationToken.None);
            await WaitUntil(
                () => ctx.Session.GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult().State
                    is PlaybackState.Playing or PlaybackState.Error,
                $"playing on cycle {i}");

            var afterPlay = await ctx.Session.GetStatusAsync(CancellationToken.None);
            Assert.NotEqual(PlaybackState.Error, afterPlay.State);

            await WaitUntil(() => ctx.Presenter.IsVisible, $"visible on cycle {i}");

            await ctx.Session.StopAsync(CancellationToken.None);
            await WaitUntil(() => !ctx.Presenter.IsVisible, $"hidden on cycle {i}");
        }

        // A repeated Play without an intervening Stop is a no-op for the window, not a toggle.
        await ctx.Session.PlayAsync(CancellationToken.None);
        await WaitUntil(() => ctx.Presenter.IsVisible, "visible after final play");
        await ctx.Session.PlayAsync(CancellationToken.None);
        await Task.Delay(300);
        Assert.True(ctx.Presenter.IsVisible, "a repeated Play must not toggle the overlay");
        Assert.Equal("fullscreen", ctx.Presenter.PresentationState);
    }

    [MpvFFmpegFact]
    public async Task A_failed_load_leaves_no_overlay_behind()
    {

        await using var ctx = await SessionContext.CreateAsync();

        // Unreachable media: the control point went away, or sent a dead URL.
        var dead = new Uri("http://127.0.0.1:1/missing.mp4");
        await ctx.Session.LoadAsync(dead, Didl(dead.ToString()), CancellationToken.None);

        await WaitUntil(
            () => ctx.Session.GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult().State == PlaybackState.Error,
            "error state");

        Assert.False(ctx.Presenter.IsVisible, "a failed load must not leave a blank fullscreen window");
    }

    [MpvFFmpegFact]
    public async Task Pause_and_seek_do_not_drop_topmost()
    {
        var media = CreateVideo();

        await using var ctx = await SessionContext.CreateAsync();
        await ctx.Session.LoadAsync(new Uri(media), Didl(media), CancellationToken.None);
        await ctx.Session.PlayAsync(CancellationToken.None);
        await WaitUntil(() => ctx.Presenter.IsVisible, "visible after play");

        await ctx.Session.SeekAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        await ctx.Session.PauseAsync(CancellationToken.None);
        await Task.Delay(500);

        Assert.True(ctx.Presenter.IsVisible);
        Assert.True(ctx.Presenter.IsTopmost, "seek and pause must not drop the overlay out of the topmost band");
    }

    [MpvFFmpegFact]
    public async Task Snapshot_reports_the_same_state_the_overlay_follows()
    {
        var media = CreateVideo();

        await using var ctx = await SessionContext.CreateAsync();
        await ctx.Session.LoadAsync(new Uri(media), Didl(media), CancellationToken.None);
        await ctx.Session.PlayAsync(CancellationToken.None);
        await WaitUntil(() => ctx.Presenter.IsVisible, "visible after play");

        // The overlay decision is derived from this same snapshot model, so they cannot disagree.
        var snapshot = ctx.Session.GetSnapshot();
        Assert.NotEqual(PlaybackState.Stopped, snapshot.Playback.State);
        Assert.True(ctx.Presenter.IsVisible);

        await ctx.Session.StopAsync(CancellationToken.None);
        await WaitUntil(() => ctx.Session.GetSnapshot().Playback.State == PlaybackState.Stopped, "stopped snapshot");
        await WaitUntil(() => !ctx.Presenter.IsVisible, "hidden after stop");
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < Timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException($"timed out waiting for: {what}");
    }

    /// <summary>Short clip generated by ffmpeg; the test is skipped when ffmpeg is missing.</summary>
    private string CreateVideo(int seconds = 10)
    {
        var path = Path.Combine(_work, $"clip-{Guid.NewGuid():N}.mp4");
        var arguments =
            $"-y -v error -f lavfi -i testsrc=size=160x120:rate=15:duration={seconds} " +
            $"-c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{path}\"";

        var start = new ProcessStartInfo("ffmpeg", arguments)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("ffmpeg could not be started; install it or drop these tests.");

        process.WaitForExit(60_000);

        Assert.True(
            File.Exists(path) && new FileInfo(path).Length > 0,
            $"ffmpeg could not produce a fixture: {process.StandardError.ReadToEnd()}");

        return path;
    }

    private static string Didl(string uri)
        => $"""
            <DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"
                       xmlns:dc="http://purl.org/dc/elements/1.1/"
                       xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/">
              <item id="1" parentID="0" restricted="1">
                <dc:title>Lifecycle fixture</dc:title>
                <upnp:class>object.item.videoItem</upnp:class>
                <res protocolInfo="http-get:*:video/mp4:*">{uri}</res>
              </item>
            </DIDL-Lite>
            """;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);
}
