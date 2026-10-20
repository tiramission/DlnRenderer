namespace DlnRenderer.Core.Models;

/// <summary>
/// Single source of truth for what the renderer is doing.
/// Both the IPC layer and the DLNA state machine project from this model so the UI,
/// AVTransport and RenderingControl responses cannot drift apart.
/// </summary>
public sealed record PlaybackStatus
{
    public static PlaybackStatus Empty { get; } = new();

    public PlaybackState State { get; init; } = PlaybackState.Stopped;

    public MediaInfo? Media { get; init; }

    public TimeSpan Position { get; init; }

    /// <summary>Duration reported by the player; may differ from <see cref="MediaInfo.Duration"/>.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Streams actually present in the loaded media, as reported by the player.</summary>
    public MediaKind MediaKind { get; init; } = MediaKind.Unknown;

    /// <summary>Player volume in the range 0-100. This is the player's own volume, not the Windows master volume.</summary>
    public int Volume { get; init; } = 100;

    public bool Muted { get; init; }

    /// <summary>Populated only when <see cref="State"/> is <see cref="PlaybackState.Error"/>.</summary>
    public string? ErrorMessage { get; init; }
}
