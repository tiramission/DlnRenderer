using DlnRenderer.Core.Models;

namespace DlnRenderer.Player.Vlc;

/// <summary>Native libvlc transport signals that determine the domain state.</summary>
/// <param name="HasError">The current media reported <c>EncounteredError</c>.</param>
/// <param name="ReachedEnd">The current media ended on its own (not stopped by a command).</param>
/// <param name="IsStopped">libvlc state is <c>Stopped</c> or <c>NothingSpecial</c>.</param>
/// <param name="IsOpening">libvlc state is <c>Opening</c>: a medium was accepted and is being opened.</param>
/// <param name="IsBuffering">A <c>Buffering</c> event reported a cache below 100%.</param>
/// <param name="IsPaused">libvlc state is <c>Paused</c>.</param>
/// <param name="IsPlaying">libvlc state is <c>Playing</c>.</param>
public readonly record struct VlcTransportSnapshot(
    bool HasError,
    bool ReachedEnd,
    bool IsStopped,
    bool IsOpening,
    bool IsBuffering,
    bool IsPaused,
    bool IsPlaying);

/// <summary>
/// Translates native libvlc signals into the domain playback state.
/// Kept pure so the mapping can be unit tested without loading libvlc.
/// </summary>
public static class VlcStateMapper
{
    public static PlaybackState Map(VlcTransportSnapshot snapshot)
    {
        if (snapshot.HasError)
        {
            return PlaybackState.Error;
        }

        if (snapshot.ReachedEnd || snapshot.IsStopped)
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

        if (snapshot.IsOpening)
        {
            return PlaybackState.Transitioning;
        }

        if (snapshot.IsPlaying)
        {
            return PlaybackState.Playing;
        }

        // No signal at all describes the player before its first medium: idle.
        return PlaybackState.Stopped;
    }
}
