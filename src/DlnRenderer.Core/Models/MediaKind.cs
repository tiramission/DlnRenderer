namespace DlnRenderer.Core.Models;

/// <summary>
/// What kind of streams the currently loaded media actually contains, as reported by the player
/// (not as declared by the control point, which is often wrong or missing).
/// </summary>
public enum MediaKind
{
    Unknown,

    Audio,

    Video,
}
