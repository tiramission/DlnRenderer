using DlnRenderer.Core.Models;

namespace DlnRenderer.Core.Interfaces;

/// <summary>
/// Playback engine abstraction. The libmpv adapter implements this so that neither
/// the UI nor the DLNA protocol layer depends on native player calls.
/// </summary>
public interface IMediaPlayer : IAsyncDisposable
{
    /// <summary>Raised whenever the player state changes, from any thread.</summary>
    event EventHandler<PlaybackStatus>? StatusChanged;

    /// <summary>Loads <paramref name="media"/> without starting playback.</summary>
    Task LoadAsync(MediaInfo media, CancellationToken cancellationToken);

    Task PlayAsync(CancellationToken cancellationToken);

    Task PauseAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken);

    /// <param name="volume">Player volume in the range 0-100.</param>
    Task SetVolumeAsync(int volume, CancellationToken cancellationToken);

    Task SetMuteAsync(bool muted, CancellationToken cancellationToken);

    /// <summary>Returns a snapshot of the current transport state.</summary>
    Task<PlaybackStatus> GetStatusAsync(CancellationToken cancellationToken);
}
