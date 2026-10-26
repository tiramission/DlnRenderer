using DlnRenderer.Core.Models;

namespace DlnRenderer.Player.Vlc;

/// <summary>Construction-time settings for <see cref="VlcPlayer"/>.</summary>
public sealed record VlcPlayerOptions
{
    /// <summary>
    /// Handle of the child window libvlc should render video into, as reported by the service's
    /// video surface. Zero means "no video output window" (audio/decode only).
    /// </summary>
    public nint VideoWindowHandle { get; init; }

    /// <summary>
    /// Requests hardware decoding. libvlc 3 defaults to picking a decoder automatically, so
    /// enabling needs no option; disabling pins <c>avcodec-hw</c> to <c>none</c>.
    /// </summary>
    public bool HardwareDecoding { get; init; } = true;

    /// <summary>How often progress updates may be published. Higher frequencies are dropped.</summary>
    public TimeSpan PositionUpdateInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Forward libvlc log lines at or above this verbosity (0 debug, 1 notice, 2 warning, 3 error).</summary>
    public int LogVerbosity { get; init; } = 2;
}
