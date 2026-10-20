using System.Buffers.Binary;
using System.IO.Pipes;
using DlnRenderer.Core.Ipc;
using DlnRenderer.Core.Models;
using DlnRenderer.Service.Hosting;
using DlnRenderer.Service.Ipc;
using DlnRenderer.Service.Tests.Support;

namespace DlnRenderer.Service.Tests;

public class IpcServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed class Harness : IAsyncDisposable
    {
        public FakeRendererControl Renderer { get; } = new();

        public TaskCompletionSource<bool> Shutdown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string PipeName { get; } = $"DlnRenderer.Test.{Guid.NewGuid():N}";

        public IpcServer Server { get; private set; } = null!;

        public void Start()
        {
            Server = new IpcServer(Renderer, new ServiceLog(), () => Shutdown.TrySetResult(true), PipeName);
            Server.Start();
        }

        public async Task<NamedPipeClientStream> ConnectAsync()
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (true)
            {
                var client = IpcFraming.CreateClientStream(PipeName);
                try
                {
                    await client.ConnectAsync(250);
                    return client;
                }
                catch (Exception ex) when (ex is TimeoutException or IOException)
                {
                    await client.DisposeAsync();
                    if (DateTime.UtcNow > deadline)
                    {
                        throw new TimeoutException("The IPC test server did not accept a connection.", ex);
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
        }
    }

    private static async Task<IpcMessage> SendAsync(
        Stream client,
        string command,
        string? requestId = null,
        string version = IpcProtocol.Version,
        double? seekSeconds = null,
        int? volume = null)
    {
        var id = requestId ?? Guid.NewGuid().ToString("N");
        await IpcFraming.WriteAsync(
            client,
            new IpcMessage
            {
                Type = IpcMessageTypes.Request,
                Version = version,
                RequestId = id,
                Command = command,
                SeekSeconds = seekSeconds,
                Volume = volume,
            },
            CancellationToken.None);

        using var timeout = new CancellationTokenSource(Timeout);
        while (true)
        {
            var message = await IpcFraming.ReadAsync(client, timeout.Token)
                ?? throw new EndOfStreamException("The server closed the connection.");

            if (message.Type == IpcMessageTypes.Response && message.RequestId == id)
            {
                return message;
            }
        }
    }

    [Fact]
    public async Task Client_receives_an_initial_snapshot_and_status()
    {
        await using var harness = new Harness();
        harness.Start();

        await using var client = await harness.ConnectAsync();
        using var timeout = new CancellationTokenSource(Timeout);
        var hello = await IpcFraming.ReadAsync(client, timeout.Token);

        Assert.NotNull(hello);
        Assert.Equal(IpcMessageTypes.Event, hello.Type);
        Assert.Equal("Test Renderer", hello.Snapshot!.DeviceName);

        var response = await SendAsync(client, IpcCommands.GetStatus);
        Assert.True(response.Success);
        Assert.Equal(PlaybackState.Stopped, response.Snapshot!.Playback.State);
    }

    [Fact]
    public async Task Transport_commands_reach_the_renderer_and_return_the_new_state()
    {
        await using var harness = new Harness();
        harness.Start();
        await using var client = await harness.ConnectAsync();

        var play = await SendAsync(client, IpcCommands.Play);
        Assert.True(play.Success);
        Assert.Equal(PlaybackState.Playing, play.Snapshot!.Playback.State);

        var pause = await SendAsync(client, IpcCommands.Pause);
        Assert.True(pause.Success);
        Assert.Equal(PlaybackState.Paused, pause.Snapshot!.Playback.State);

        var stop = await SendAsync(client, IpcCommands.Stop);
        Assert.True(stop.Success);
        Assert.Equal(PlaybackState.Stopped, stop.Snapshot!.Playback.State);

        var seek = await SendAsync(client, IpcCommands.Seek, seekSeconds: 12.5);
        Assert.True(seek.Success);
        Assert.Equal(12.5, harness.Renderer.LastSeek!.Value.TotalSeconds, 3);

        var volume = await SendAsync(client, IpcCommands.SetVolume, volume: 40);
        Assert.True(volume.Success);
        Assert.Equal(40, harness.Renderer.LastVolume);

        Assert.Equal(["play", "pause", "stop", "seek:12.5", "volume:40"], harness.Renderer.Calls);
    }

    [Theory]
    [InlineData("SetVolume", null, null, "Volume")]
    [InlineData("SetVolume", null, 101, "Volume")]
    [InlineData("Seek", null, null, "SeekSeconds")]
    [InlineData("Seek", -1.0, null, "SeekSeconds")]
    public async Task Invalid_arguments_are_rejected_without_touching_the_renderer(
        string command,
        double? seekSeconds,
        int? volume,
        string expectedErrorFragment)
    {
        await using var harness = new Harness();
        harness.Start();
        await using var client = await harness.ConnectAsync();

        var response = await SendAsync(client, command, seekSeconds: seekSeconds, volume: volume);

        Assert.False(response.Success);
        Assert.Contains(expectedErrorFragment, response.Error, StringComparison.Ordinal);
        Assert.Empty(harness.Renderer.Calls);
    }

    [Fact]
    public async Task Unknown_command_and_wrong_version_report_errors()
    {
        await using var harness = new Harness();
        harness.Start();
        await using var client = await harness.ConnectAsync();

        var unknown = await SendAsync(client, "FormatDisk");
        Assert.False(unknown.Success);
        Assert.Contains("unsupported command", unknown.Error, StringComparison.OrdinalIgnoreCase);

        var oldVersion = await SendAsync(client, IpcCommands.GetStatus, version: "0.9");
        Assert.False(oldVersion.Success);
        Assert.Contains("version", oldVersion.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Shutdown_command_invokes_the_host_callback()
    {
        await using var harness = new Harness();
        harness.Start();
        await using var client = await harness.ConnectAsync();

        var response = await SendAsync(client, "Shutdown");

        Assert.True(response.Success);
        Assert.True(await harness.Shutdown.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Snapshot_changes_are_pushed_to_connected_clients()
    {
        await using var harness = new Harness();
        harness.Start();
        await using var client = await harness.ConnectAsync();

        using var timeout = new CancellationTokenSource(Timeout);
        await IpcFraming.ReadAsync(client, timeout.Token); // initial snapshot

        harness.Renderer.Publish(new RendererSnapshot
        {
            DeviceName = "Updated",
            Playback = new PlaybackStatus { State = PlaybackState.Playing },
        });

        var received = await IpcFraming.ReadAsync(client, timeout.Token);
        Assert.Equal(IpcMessageTypes.Event, received!.Type);
        Assert.Equal("Updated", received.Snapshot!.DeviceName);
        Assert.Equal(PlaybackState.Playing, received.Snapshot.Playback.State);
    }

    [Fact]
    public async Task A_protocol_violation_drops_only_that_client()
    {
        await using var harness = new Harness();
        harness.Start();

        var bad = await harness.ConnectAsync();
        using (var timeout = new CancellationTokenSource(Timeout))
        {
            await IpcFraming.ReadAsync(bad, timeout.Token); // initial snapshot
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, -1);
        await bad.WriteAsync(header);
        await bad.FlushAsync();

        // The server must close the connection on a bad frame. A clean close surfaces as a null
        // read (end of stream); a reset surfaces as an exception. Both mean "dropped".
        using (var timeout = new CancellationTokenSource(Timeout))
        {
            IpcMessage? afterViolation;
            try
            {
                afterViolation = await IpcFraming.ReadAsync(bad, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException)
            {
                afterViolation = null;
            }

            Assert.Null(afterViolation);
        }

        await bad.DisposeAsync();

        await using var good = await harness.ConnectAsync();
        var response = await SendAsync(good, IpcCommands.GetStatus);
        Assert.True(response.Success);
    }

    [Fact]
    public async Task Disconnecting_a_client_leaves_the_server_listening()
    {
        await using var harness = new Harness();
        harness.Start();

        var client = await harness.ConnectAsync();
        using (var timeout = new CancellationTokenSource(Timeout))
        {
            await IpcFraming.ReadAsync(client, timeout.Token);
        }

        await client.DisposeAsync();

        var deadline = DateTime.UtcNow + Timeout;
        while (harness.Server.ConnectedClients != 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.Equal(0, harness.Server.ConnectedClients);

        await using var reconnect = await harness.ConnectAsync();
        var response = await SendAsync(reconnect, IpcCommands.GetStatus);
        Assert.True(response.Success);
    }

    [Fact]
    public async Task Multiple_clients_can_issue_commands_independently()
    {
        await using var harness = new Harness();
        harness.Start();

        await using var first = await harness.ConnectAsync();
        await using var second = await harness.ConnectAsync();

        var firstPlay = await SendAsync(first, IpcCommands.Play);
        var secondStatus = await SendAsync(second, IpcCommands.GetStatus);

        Assert.True(firstPlay.Success);
        Assert.True(secondStatus.Success);
        Assert.Equal(PlaybackState.Playing, secondStatus.Snapshot!.Playback.State);
    }

    [Fact]
    public async Task Renderer_failures_are_reported_instead_of_faked_as_success()
    {
        await using var harness = new Harness();
        harness.Renderer.FailTransport = "No media has been set; Play is not available.";
        harness.Start();
        await using var client = await harness.ConnectAsync();

        var response = await SendAsync(client, IpcCommands.Play);

        // A player that cannot act must never look like a successful play.
        Assert.False(response.Success);
        Assert.Contains("No media", response.Error, StringComparison.Ordinal);
    }
}
