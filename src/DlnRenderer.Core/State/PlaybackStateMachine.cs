namespace DlnRenderer.Core.State;

using DlnRenderer.Core.Models;

/// <summary>Raised when a caller attempts an illegal transport transition.</summary>
public sealed class InvalidPlaybackTransitionException(PlaybackState from, PlaybackState to)
    : InvalidOperationException($"Cannot transition playback state from '{from}' to '{to}'.")
{
    public PlaybackState From { get; } = from;

    public PlaybackState To { get; } = to;
}

/// <summary>
/// Guards the single authoritative playback state shared by the IPC and DLNA layers.
/// </summary>
public sealed class PlaybackStateMachine
{
    private readonly Lock _gate = new();
    private PlaybackState _current = PlaybackState.Stopped;

    public PlaybackState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Raised after a successful transition. Handlers run outside the internal lock.</summary>
    public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;

    public bool CanTransitionTo(PlaybackState target)
    {
        lock (_gate)
        {
            return PlaybackStateRules.CanTransition(_current, target);
        }
    }

    /// <summary>Applies <paramref name="target"/> and returns <c>false</c> when the transition is illegal.</summary>
    public bool TryTransitionTo(PlaybackState target)
    {
        PlaybackState from;
        lock (_gate)
        {
            from = _current;
            if (!PlaybackStateRules.CanTransition(from, target))
            {
                return false;
            }

            _current = target;
        }

        if (from != target)
        {
            StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(from, target));
        }

        return true;
    }

    /// <summary>Applies <paramref name="target"/>, throwing when the transition is illegal.</summary>
    /// <exception cref="InvalidPlaybackTransitionException">The transition is not permitted.</exception>
    public void TransitionTo(PlaybackState target)
    {
        if (!TryTransitionTo(target))
        {
            throw new InvalidPlaybackTransitionException(Current, target);
        }
    }
}

/// <summary>Describes a completed transition.</summary>
public sealed class PlaybackStateChangedEventArgs(PlaybackState from, PlaybackState to) : EventArgs
{
    public PlaybackState From { get; } = from;

    public PlaybackState To { get; } = to;
}
