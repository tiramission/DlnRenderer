namespace DlnRenderer.Core.Models;

/// <summary>Where a cast's picture is presented.</summary>
public enum DisplayMode
{
    /// <summary>
    /// Borderless, always-on-top window covering a monitor. Works with no UI attached, which is
    /// why it stays the default.
    /// </summary>
    Fullscreen,

    /// <summary>
    /// Draws into an area of the application window. Requires the UI to be running and to report
    /// the area it reserved; without a host the renderer falls back to <see cref="Fullscreen"/>
    /// rather than showing nothing.
    /// </summary>
    Embedded,
}
