namespace DlnRenderer.Player;

/// <summary>
/// Rate-limits progress notifications. mpv reports <c>time-pos</c> far more often than a UI can
/// use; forwarding every change would keep the UI thread saturated.
/// Not thread safe by itself: callers serialise access (the player calls it from one thread).
/// </summary>
public sealed class PositionThrottle(TimeSpan minimumInterval)
{
    private TimeSpan _lastPublished = TimeSpan.MinValue;

    public TimeSpan MinimumInterval { get; } = minimumInterval;

    /// <summary>Returns <c>true</c> when a sample taken at <paramref name="elapsed"/> may be published.</summary>
    public bool ShouldPublish(TimeSpan elapsed)
    {
        if (_lastPublished == TimeSpan.MinValue || elapsed - _lastPublished >= MinimumInterval)
        {
            _lastPublished = elapsed;
            return true;
        }

        return false;
    }

    /// <summary>Forces the next sample to be published, e.g. after a seek or state change.</summary>
    public void Reset() => _lastPublished = TimeSpan.MinValue;
}
