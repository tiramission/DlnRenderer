namespace DlnRenderer.Core.Models;

/// <summary>
/// Describes the media currently offered by a control point.
/// Fields are optional because DIDL-Lite metadata is frequently incomplete.
/// </summary>
/// <param name="MediaUri">The URI the player must fetch. Never a local path unless the control point sent one.</param>
/// <param name="Title">Human readable title; falls back to the URI file name when the control point omits it.</param>
/// <param name="MimeType">Declared MIME type, used for capability checks and player hints.</param>
/// <param name="Duration">Declared duration, when the control point provides one.</param>
public sealed record MediaInfo(Uri MediaUri)
{
    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? Album { get; init; }

    public Uri? CoverUri { get; init; }

    public string? MimeType { get; init; }

    public TimeSpan? Duration { get; init; }
}
