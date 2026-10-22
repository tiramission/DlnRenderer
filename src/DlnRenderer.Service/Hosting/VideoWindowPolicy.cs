using System;
using DlnRenderer.Core.Models;

namespace DlnRenderer.Service.Hosting;

/// <summary>What the presentation window should be doing right now.</summary>
public enum VideoWindowMode
{
    /// <summary>Window hidden; nothing is being presented.</summary>
    Hidden,

    /// <summary>
    /// The picture belongs on screen. Where it goes - a fullscreen overlay or a rectangle inside the
    /// application window - is the presenter's decision, taken from the target the UI reported.
    /// </summary>
    Shown,
}

/// <summary>
/// Decides whether the presentation window is visible.
/// </summary>
/// <remarks>
/// This is pure policy with no Win32 dependency so it can be tested exhaustively. The window
/// itself (<see cref="VideoPresenter"/>) only executes the decision.
/// <para>
/// The intentional asymmetry: showing is gated on <see cref="PlaybackState.Playing"/>, hiding is
/// gated only on the terminal states. A control point that pauses, seeks, buffers, or swaps media
/// therefore keeps the window on screen; only Stop/Error take it away.
/// </para>
/// </remarks>
public static class VideoWindowPolicy
{
    /// <summary>
    /// Window state for the given playback status.
    /// </summary>
    /// <param name="status">Status reported by the player.</param>
    /// <param name="requested">Whether the current URI is the one the player is actually serving.</param>
    /// <param name="lastKnownVideo">
    /// Whether the most recent status that reported a media kind described video. mpv resets the
    /// track list when a file ends or is replaced, so the kind reads <see cref="MediaKind.Unknown"/>
    /// right when the next Play is about to (re)load - using only the current status would keep the
    /// window hidden for a replayed video.
    /// </param>
    /// <param name="playbackStarted">
    /// True once Play was issued for the current medium and Stop has not happened since. Distinguishes
    /// "paused because that is how media loads" from "paused by the user".
    /// </param>
    /// <remarks>
    /// One presentation state covers both Stop and end of media: libmpv is configured with
    /// <c>keep-open=no</c>, so reaching the end of the file surfaces as
    /// <see cref="PlaybackState.Stopped"/>. Hidden is the end state of a finished playback, not a
    /// special case, which is exactly what a user expects after the film ends.
    /// </remarks>
    public static VideoWindowMode Decide(
        PlaybackStatus status,
        bool requested,
        bool lastKnownVideo = false,
        bool playbackStarted = true)
    {
        ArgumentNullException.ThrowIfNull(status);

        // Events that still describe the previous medium must not drive the window: during a switch
        // the old file reports Stopped right after the new one was requested, and acting on that
        // would hide the window the new medium is about to use.
        if (!requested)
        {
            return VideoWindowMode.Hidden;
        }

        if (status.State is PlaybackState.Stopped or PlaybackState.Error)
        {
            return VideoWindowMode.Hidden;
        }

        // Audio-only media has no picture to present, so the screen stays with the user.
        var video = status.MediaKind == MediaKind.Video
            || (status.MediaKind == MediaKind.Unknown && lastKnownVideo);
        if (!video)
        {
            return VideoWindowMode.Hidden;
        }

        // Paused has two very different meanings and they must not be conflated:
        //  * a prepared load - libmpv opens every file paused, so SetAVTransportURI/SetNextURI lands
        //    here and the user has not asked to watch anything yet;
        //  * a paused cast - Play was pressed and then Pause.
        // Only the second must keep the overlay. The caller marks it with `playbackStarted`, which is
        // set by Play and cleared by Stop.
        if (status.State == PlaybackState.Paused && !playbackStarted)
        {
            return VideoWindowMode.Hidden;
        }

        // Playing, Paused, Buffering and Transitioning all keep the overlay up. Hiding on Pause would
        // make a paused cast look like it had been dismissed, and hiding while buffering would
        // flash the desktop in the middle of a stutter.
        return VideoWindowMode.Shown;
    }
}
