using System.Diagnostics;
using DlnRenderer.Core.Models;
using DlnRenderer.Service.Hosting;

namespace DlnRenderer.Service.Tests;

/// <summary>
/// Seeking, driven through the real session and the real player.
/// </summary>
/// <remarks>
/// Progress control is the part of casting users touch most, and the part that fails most visibly:
/// a seek that blocks or faults makes the remote slider look dead while the media plays on. The
/// cases below are the shapes casting clients actually produce - seek while playing, and seek after
/// the medium ended (the phone restores a position, or the user drags the bar back after the clip
/// finished).
/// </remarks>
[Collection("video-window")]
public class SeekBehaviourTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _work = Path.Combine(
        Path.GetTempPath(),
        "dlnrenderer-seek-tests",
        Guid.NewGuid().ToString("N"));

    public SeekBehaviourTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // A scratch directory that cannot be deleted must not mask a result.
        }
    }

    [MpvFFmpegFact]
    public async Task Seek_while_playing_moves_the_player()
    {
        var media = CreateVideo(seconds: 20);
        await using var ctx = await SessionContext.CreateAsync();

        await ctx.Session.LoadAsync(new Uri(media), Didl(media), CancellationToken.None);
        await WaitUntil(
            async () => (await ctx.Session.GetStatusAsync(CancellationToken.None)).State == PlaybackState.Paused,
            "media loaded and paused");

        await ctx.Session.PlayAsync(CancellationToken.None);
        await WaitUntil(
            async () => (await ctx.Session.GetStatusAsync(CancellationToken.None)).State == PlaybackState.Playing,
            "playing");

        await ctx.Session.SeekAsync(TimeSpan.FromSeconds(15), CancellationToken.None);

        await WaitUntil(
            async () => (await ctx.Session.GetStatusAsync(CancellationToken.None)).Position.TotalSeconds > 14,
            "player moved to the seek target");
    }

    [MpvFFmpegFact]
    public async Task Seek_after_stop_returns_at_once_and_is_applied_by_the_reload()
    {
        var media = CreateVideo(seconds: 20);
        await using var ctx = await SessionContext.CreateAsync();

        await ctx.Session.LoadAsync(new Uri(media), Didl(media), CancellationToken.None);
        await WaitUntil(
            async () => (await ctx.Session.GetStatusAsync(CancellationToken.None)).State == PlaybackState.Paused,
            "media loaded and paused");

        await ctx.Session.PlayAsync(CancellationToken.None);
        await WaitUntil(
            async () => (await ctx.Session.GetStatusAsync(CancellationToken.None)).State == PlaybackState.Playing,
            "playing");

        await ctx.Session.StopAsync(CancellationToken.None);

        // Nothing is loaded at this point. The control point must still be answered immediately:
        // waiting for a reload here is what made a remote progress bar unresponsive for seconds.
        var elapsed = Stopwatch.StartNew();
        await ctx.Session.SeekAsync(TimeSpan.FromSeconds(12), CancellationToken.None);
        elapsed.Stop();

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(3),
            $"Seek did not return promptly; it took {elapsed.Elapsed.TotalSeconds:F1}s");

        // ...and the position still takes effect, once the renderer has reloaded the medium.
        await WaitUntil(
            async () =>
            {
                var status = await ctx.Session.GetStatusAsync(CancellationToken.None);
                return status.State is PlaybackState.Paused or PlaybackState.Playing
                    && status.Position.TotalSeconds > 10;
            },
            "reload applied the deferred seek");
    }

    [MpvFFmpegFact]
    public async Task Play_after_seek_starts_at_the_requested_position()
    {
        var media = CreateVideo(seconds: 20);
        await using var ctx = await SessionContext.CreateAsync();

        await ctx.Session.LoadAsync(new Uri(media), Didl(media), CancellationToken.None);
        await WaitUntil(
            async () => (await ctx.Session.GetStatusAsync(CancellationToken.None)).State == PlaybackState.Paused,
            "media loaded and paused");

        await ctx.Session.StopAsync(CancellationToken.None);

        // The order a client restoring a session uses: position first, then play.
        await ctx.Session.SeekAsync(TimeSpan.FromSeconds(9), CancellationToken.None);
        await ctx.Session.PlayAsync(CancellationToken.None);

        await WaitUntil(
            async () =>
            {
                var status = await ctx.Session.GetStatusAsync(CancellationToken.None);
                return status.State == PlaybackState.Playing && status.Position.TotalSeconds >= 8.5;
            },
            "playback resumed at the requested position");
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException($"timed out waiting for: {what}");
    }

    /// <summary>Short clip generated by ffmpeg; the test is skipped when ffmpeg is missing.</summary>
    private string CreateVideo(int seconds)
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
                <dc:title>Seek fixture</dc:title>
                <upnp:class>object.item.videoItem</upnp:class>
                <res protocolInfo="http-get:*:video/mp4:*">{uri}</res>
              </item>
            </DIDL-Lite>
            """;
}
