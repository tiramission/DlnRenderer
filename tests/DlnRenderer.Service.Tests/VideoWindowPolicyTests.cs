using DlnRenderer.Core.Models;
using DlnRenderer.Service.Hosting;

namespace DlnRenderer.Service.Tests;

/// <summary>
/// Window lifecycle decisions: show on play, stay up while paused, come down on stop, end or error.
/// </summary>
/// <remarks>
/// These assertions describe the product contract rather than implementation details, so a
/// regression in the presenter wiring shows up here even on a machine where no window can be created.
/// </remarks>
public class VideoWindowPolicyTests
{
    /// <summary>
    /// Status as reported by a player that has been asked to play: the overlay follows it.
    /// </summary>
    private static PlaybackStatus Status(PlaybackState state, MediaKind kind = MediaKind.Video)
        => new() { State = state, MediaKind = kind };

    [Theory]
    [InlineData(PlaybackState.Playing)]
    [InlineData(PlaybackState.Paused)]
    [InlineData(PlaybackState.Buffering)]
    [InlineData(PlaybackState.Transitioning)]
    public void Video_that_is_playing_covers_the_screen(PlaybackState state)
        => Assert.Equal(
            VideoWindowMode.Shown,
            VideoWindowPolicy.Decide(Status(state), requested: true, playbackStarted: true));

    [Theory]
    [InlineData(PlaybackState.Stopped)]
    [InlineData(PlaybackState.Error)]
    public void Stop_and_error_take_the_overlay_down(PlaybackState state)
    {
        // Stopped is also what libmpv reports when the file runs out (keep-open=no), so this one
        // assertion covers "playback finished" and "playback stopped" alike.
        Assert.Equal(
            VideoWindowMode.Hidden,
            VideoWindowPolicy.Decide(Status(state), requested: true, playbackStarted: true));
    }

    [Fact]
    public void A_prepared_load_is_paused_but_must_not_take_the_screen()
    {
        // SetAVTransportURI opens the file paused. Without this distinction a control point that only
        // prepares media - or the user browsing a playlist - would black out the screen.
        Assert.Equal(
            VideoWindowMode.Hidden,
            VideoWindowPolicy.Decide(
                Status(PlaybackState.Paused),
                requested: true,
                lastKnownVideo: true,
                playbackStarted: false));
    }

    [Fact]
    public void Pause_after_play_keeps_a_paused_cast_on_screen()
    {
        // Same status, different intent: Play was pressed, so Pause is deliberate and the picture stays.
        Assert.Equal(
            VideoWindowMode.Shown,
            VideoWindowPolicy.Decide(
                Status(PlaybackState.Paused),
                requested: true,
                lastKnownVideo: true,
                playbackStarted: true));
    }

    [Theory]
    [InlineData(PlaybackState.Playing)]
    [InlineData(PlaybackState.Paused)]
    public void Audio_only_media_does_not_take_the_screen(PlaybackState state)
        => Assert.Equal(
            VideoWindowMode.Hidden,
            VideoWindowPolicy.Decide(Status(state, MediaKind.Audio), requested: true, playbackStarted: true));

    [Fact]
    public void Unknown_media_kind_does_not_take_the_screen_before_a_video_was_seen()
    {
        // Before the first video frame is decoded the kind is Unknown; a black fullscreen window
        // would be worse than waiting one update.
        Assert.Equal(
            VideoWindowMode.Hidden,
            VideoWindowPolicy.Decide(
                Status(PlaybackState.Transitioning, MediaKind.Unknown),
                requested: true,
                lastKnownVideo: false,
                playbackStarted: true));
    }

    [Fact]
    public void A_replayed_video_still_shows_while_the_player_reports_no_kind_yet()
    {
        // mpv resets its track list when a file ends or is replaced, so the kind reads Unknown right
        // before the next Play reloads it. Without remembering the last known kind, replaying a video
        // would leave the overlay hidden.
        Assert.Equal(
            VideoWindowMode.Shown,
            VideoWindowPolicy.Decide(
                Status(PlaybackState.Buffering, MediaKind.Unknown),
                requested: true,
                lastKnownVideo: true,
                playbackStarted: true));
    }

    [Fact]
    public void An_audio_only_file_is_not_presented_even_after_a_video()
    {
        Assert.Equal(
            VideoWindowMode.Hidden,
            VideoWindowPolicy.Decide(
                Status(PlaybackState.Playing, MediaKind.Audio),
                requested: true,
                lastKnownVideo: true,
                playbackStarted: true));
    }

    [Fact]
    public void Events_for_replaced_media_never_drive_the_window()
    {
        // During a switch the outgoing file reports Stopped after the new URI was already requested;
        // acting on it would hide the overlay the incoming medium is about to use.
        Assert.Equal(
            VideoWindowMode.Hidden,
            VideoWindowPolicy.Decide(Status(PlaybackState.Playing), requested: false));
    }

    [Fact]
    public void Policy_requires_a_status()
        => Assert.Throws<ArgumentNullException>(() => VideoWindowPolicy.Decide(null!, requested: true));
}
