using DlnRenderer.Core.Models;

namespace DlnRenderer.Dlna.AvTransport;

/// <summary>Snapshot of what the underlying player is actually doing.</summary>
public sealed record TransportSnapshot(
    PlaybackState State,
    TimeSpan Position,
    TimeSpan? Duration,
    string? Title,
    string? Error);

/// <summary>
/// Adapter the service implements over the existing libmpv player. AVTransport never sees
/// native player types, which keeps the protocol layer testable with a fake.
/// </summary>
public interface ITransportRenderer
{
    Uri? CurrentUri { get; }

    string? CurrentMetadata { get; }

    /// <summary>Loads the media in a paused state; completes when the player reports loaded or failed.</summary>
    Task LoadAsync(Uri uri, string? metadataXml, CancellationToken cancellationToken);

    Task PlayAsync(CancellationToken cancellationToken);

    Task PauseAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken);

    Task<TransportSnapshot> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Current player volume in the range 0-100, as reported by the player itself.
    /// This is the player's own volume, never the Windows master volume.
    /// </summary>
    Task<int> GetVolumeAsync(CancellationToken cancellationToken);

    /// <param name="volume">Target player volume in the range 0-100.</param>
    Task SetVolumeAsync(int volume, CancellationToken cancellationToken);

    Task<bool> GetMuteAsync(CancellationToken cancellationToken);

    Task SetMuteAsync(bool muted, CancellationToken cancellationToken);
}
