using DlnRenderer.Core.Models;
using DlnRenderer.Dlna.AvTransport;

namespace DlnRenderer.Dlna.Tests.Support;

internal sealed class FakeTransportRenderer : ITransportRenderer
{
    public Uri? CurrentUri { get; private set; }

    public string? CurrentMetadata { get; private set; }

    public PlaybackState State { get; set; } = PlaybackState.Stopped;

    public TimeSpan Position { get; set; }

    public TimeSpan? Duration { get; set; }

    public string? Error { get; set; }

    public bool FailLoad { get; set; }

    /// <summary>When set, <see cref="PlayAsync"/> throws, mirroring a player that refuses to start.</summary>
    public string? FailPlay { get; set; }

    /// <summary>When set, <see cref="SetVolumeAsync"/> throws, mirroring an unavailable player.</summary>
    public string? FailVolume { get; set; }

    public List<string> Calls { get; } = [];

    public Task LoadAsync(Uri uri, string? metadataXml, CancellationToken cancellationToken)
    {
        Calls.Add($"load:{uri}");
        if (FailLoad)
        {
            throw new InvalidOperationException("simulated load failure");
        }

        CurrentUri = uri;
        CurrentMetadata = metadataXml;
        State = PlaybackState.Stopped;
        Position = TimeSpan.Zero;
        Error = null;
        return Task.CompletedTask;
    }

    public Task PlayAsync(CancellationToken cancellationToken)
    {
        Calls.Add("play");
        if (FailPlay is not null)
        {
            throw new InvalidOperationException(FailPlay);
        }

        State = PlaybackState.Playing;
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        Calls.Add("pause");
        State = PlaybackState.Paused;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Calls.Add("stop");
        State = PlaybackState.Stopped;
        Position = TimeSpan.Zero;
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        Calls.Add($"seek:{position.TotalSeconds}");
        Position = position;
        return Task.CompletedTask;
    }

    public int Volume { get; set; } = 50;

    public bool Muted { get; set; }

    public Task<int> GetVolumeAsync(CancellationToken cancellationToken) => Task.FromResult(Volume);

    public Task SetVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        Calls.Add($"volume:{volume}");
        if (FailVolume is not null)
        {
            throw new InvalidOperationException(FailVolume);
        }

        Volume = volume;
        return Task.CompletedTask;
    }

    public Task<bool> GetMuteAsync(CancellationToken cancellationToken) => Task.FromResult(Muted);

    public Task SetMuteAsync(bool muted, CancellationToken cancellationToken)
    {
        Calls.Add($"mute:{muted}");
        Muted = muted;
        return Task.CompletedTask;
    }

    public Task<TransportSnapshot> GetStatusAsync(CancellationToken cancellationToken)
        => Task.FromResult(new TransportSnapshot(State, Position, Duration, CurrentUri?.ToString(), Error));
}
