namespace DlnRenderer.Core.Ipc;

/// <summary>
/// The service-side surface the IPC layer is allowed to touch. Kept narrow so the pipe server can
/// be tested without a real player or network.
/// </summary>
public interface IRendererControl
{
    /// <summary>Raised when the published snapshot changes, from any thread.</summary>
    event Action<RendererSnapshot>? SnapshotChanged;

    RendererSnapshot GetSnapshot();

    Task PlayAsync(CancellationToken cancellationToken);

    Task PauseAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken);

    Task SetVolumeAsync(int volume, CancellationToken cancellationToken);
}
