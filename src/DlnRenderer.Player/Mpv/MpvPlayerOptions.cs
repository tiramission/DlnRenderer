namespace DlnRenderer.Player.Mpv;

/// <summary>Construction-time settings for <see cref="MpvPlayer"/>.</summary>
public sealed record MpvPlayerOptions
{
    /// <summary>
    /// Handle of the child window libmpv should render video into. Must be created before the
    /// player is constructed, because mpv's <c>wid</c> option is applied at initialisation.
    /// Zero means "no video output window".
    /// </summary>
    public nint VideoWindowHandle { get; init; }

    /// <summary>Overrides the mpv <c>vo</c> driver, e.g. <c>null</c> for decode-only tests.</summary>
    public string? VideoOutput { get; init; }

    /// <summary>Overrides the mpv <c>ao</c> driver, e.g. <c>null</c> for silent test runs.</summary>
    public string? AudioOutput { get; init; }

    /// <summary>Requests hardware decoding via mpv's <c>auto-safe</c> mode.</summary>
    public bool HardwareDecoding { get; init; } = true;

    /// <summary>
    /// Forces one specific mpv <c>hwdec</c> method (e.g. <c>d3d11va</c>, <c>vaapi</c>) instead of
    /// letting <see cref="HardwareDecoding"/> pick <c>auto-safe</c>.
    /// </summary>
    /// <remarks>
    /// Production passes null: auto-safe is the right default. The override exists so a test can
    /// request a method the machine does not have (stage D, D3) and prove that mpv falls back to
    /// software decoding instead of failing the file.
    /// </remarks>
    public string? HardwareDecodingMethod { get; init; }

    /// <summary>How often progress updates may be published. Higher frequencies are dropped.</summary>
    public TimeSpan PositionUpdateInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>mpv log level forwarded to <see cref="MpvPlayer.LogMessage"/>.</summary>
    public string LogLevel { get; init; } = "warn";
}
