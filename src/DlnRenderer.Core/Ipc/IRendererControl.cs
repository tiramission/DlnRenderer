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

    Task SetMuteAsync(bool muted, CancellationToken cancellationToken);

    /// <summary>
    /// Applies where video may be presented. <c>null</c> means the UI is gone: the renderer goes back
    /// to presenting fullscreen on its own.
    /// </summary>
    void SetVideoTarget(VideoTarget? target);
}
