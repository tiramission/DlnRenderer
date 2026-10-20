using DlnRenderer.Core.Models;

namespace DlnRenderer.Player.Mpv;

/// <summary>Native transport signals that determine the domain state.</summary>
/// <param name="IsIdle">mpv <c>idle-active</c>: nothing loaded, or playback stopped.</param>
/// <param name="IsPaused">mpv <c>pause</c>.</param>
/// <param name="IsBuffering">mpv <c>core-idle</c> while a file is loaded and not paused.</param>
/// <param name="HasError">The last end-file event reported an error.</param>
public readonly record struct MpvTransportSnapshot(bool IsIdle, bool IsPaused, bool IsBuffering, bool HasError);

/// <summary>
/// Translates native transport signals into the domain playback state.
/// Kept pure so the mapping can be unit tested without loading libmpv.
/// </summary>
public static class MpvStateMapper
{
    public static PlaybackState Map(MpvTransportSnapshot snapshot)
    {
        if (snapshot.HasError)
        {
            return PlaybackState.Error;
        }

        if (snapshot.IsIdle)
        {
            return PlaybackState.Stopped;
        }

        if (snapshot.IsPaused)
        {
            return PlaybackState.Paused;
        }

        if (snapshot.IsBuffering)
        {
            return PlaybackState.Buffering;
        }

        return PlaybackState.Playing;
    }
}
