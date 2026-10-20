namespace DlnRenderer.Core.Models;

/// <summary>
/// Transport state of the renderer, as required by AGENTS.md section 4.
/// Maps to UPnP AVTransport <c>TransportState</c> values.
/// </summary>
public enum PlaybackState
{
    /// <summary>No media is loaded, or playback has been stopped.</summary>
    Stopped,

    /// <summary>Media is being presented.</summary>
    Playing,

    /// <summary>Media is loaded and position is held.</summary>
    Paused,

    /// <summary>The player is fetching or decoding data and cannot advance yet.</summary>
    Buffering,

    /// <summary>A new media URI is being loaded, or a transport operation is in flight.</summary>
    Transitioning,

    /// <summary>Playback failed; <see cref="PlaybackStatus.ErrorMessage"/> carries the reason.</summary>
    Error,
}
