namespace DlnRenderer.Core.State;

using DlnRenderer.Core.Models;

/// <summary>
/// Legal transport-state transitions. Kept separate from <see cref="PlaybackStateMachine"/>
/// so the rules can be inspected and tested without any mutable state.
/// </summary>
public static class PlaybackStateRules
{
    /// <summary>
    /// A transition to the current state is always allowed: control points routinely repeat
    /// Play/Pause/Stop, and rejecting those would surface spurious DLNA errors.
    /// </summary>
    public static bool CanTransition(PlaybackState from, PlaybackState to)
    {
        // Validate before the self-transition shortcut: an undefined value is never a legal state,
        // even when compared against itself.
        if (!Enum.IsDefined(from) || !Enum.IsDefined(to))
        {
            return false;
        }

        if (from == to)
        {
            return true;
        }

        return from switch
        {
            PlaybackState.Stopped => to is PlaybackState.Transitioning or PlaybackState.Error,
            PlaybackState.Transitioning => to is PlaybackState.Playing or PlaybackState.Paused
                or PlaybackState.Buffering or PlaybackState.Stopped or PlaybackState.Error,
            PlaybackState.Playing => to is PlaybackState.Paused or PlaybackState.Buffering
                or PlaybackState.Transitioning or PlaybackState.Stopped or PlaybackState.Error,
            PlaybackState.Paused => to is PlaybackState.Playing or PlaybackState.Buffering
                or PlaybackState.Transitioning or PlaybackState.Stopped or PlaybackState.Error,
            PlaybackState.Buffering => to is PlaybackState.Playing or PlaybackState.Paused
                or PlaybackState.Transitioning or PlaybackState.Stopped or PlaybackState.Error,
            // Recovery is explicit: leaving Error requires a new load (Transitioning) or a Stop.
            PlaybackState.Error => to is PlaybackState.Transitioning or PlaybackState.Stopped,
            _ => false,
        };
    }
}
