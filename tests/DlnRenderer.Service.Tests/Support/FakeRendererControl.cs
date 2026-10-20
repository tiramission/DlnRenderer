using DlnRenderer.Core.Ipc;
using DlnRenderer.Core.Models;

namespace DlnRenderer.Service.Tests.Support;

internal sealed class FakeRendererControl : IRendererControl
{
    private RendererSnapshot _snapshot = new()
    {
        DeviceName = "Test Renderer",
        Playback = PlaybackStatus.Empty,
    };

    public event Action<RendererSnapshot>? SnapshotChanged;

    public List<string> Calls { get; } = [];

    /// <summary>When set, transport commands throw this message, like the real session does without media.</summary>
    public string? FailTransport { get; set; }

    public TimeSpan? LastSeek { get; private set; }

    public int? LastVolume { get; private set; }

    public RendererSnapshot GetSnapshot() => _snapshot;

    public Task PlayAsync(CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        Calls.Add("play");
        Update(_snapshot.Playback with { State = PlaybackState.Playing });
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        Calls.Add("pause");
        Update(_snapshot.Playback with { State = PlaybackState.Paused });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        Calls.Add("stop");
        Update(_snapshot.Playback with { State = PlaybackState.Stopped, Position = TimeSpan.Zero });
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        Calls.Add($"seek:{position.TotalSeconds}");
        LastSeek = position;
        return Task.CompletedTask;
    }

    private void ThrowIfFailing()
    {
        if (FailTransport is not null)
        {
            throw new InvalidOperationException(FailTransport);
        }
    }

    public Task SetVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        Calls.Add($"volume:{volume}");
        LastVolume = volume;
        return Task.CompletedTask;
    }

    public void Publish(RendererSnapshot snapshot)
    {
        _snapshot = snapshot;
        SnapshotChanged?.Invoke(snapshot);
    }

    private void Update(PlaybackStatus playback)
    {
        _snapshot = _snapshot with { Playback = playback };
        SnapshotChanged?.Invoke(_snapshot);
    }
}
