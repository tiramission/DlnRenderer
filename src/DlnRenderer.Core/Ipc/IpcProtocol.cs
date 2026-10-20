using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using DlnRenderer.Core.Models;

namespace DlnRenderer.Core.Ipc;

/// <summary>Versioned Named Pipe protocol constants shared by the UI and the service.</summary>
public static class IpcProtocol
{
    public const string PipeName = "DlnRenderer.Service.v1";
    public const string Version = "1.0";

    /// <summary>Hard cap on a single framed message; protects both sides from unbounded reads.</summary>
    public const int MaxMessageBytes = 64 * 1024;

    /// <summary>
    /// Size of each pipe direction buffer, applied when the service creates the pipe.
    /// </summary>
    /// <remarks>
    /// This is not a tuning knob: Windows gives a pipe created with
    /// <see cref="System.IO.Pipes.PipeOptions.Asynchronous"/> a tiny (page-sized) default buffer.
    /// The protocol is full duplex - the service pushes snapshots while the UI sends requests - so
    /// with small buffers a push and a request that are issued at the same time fill both
    /// directions and each side blocks waiting for the other to read. A buffer comfortably larger
    /// than <see cref="MaxMessageBytes"/> keeps every single message non-blocking.
    /// Only the server can set this: <see cref="System.IO.Pipes.NamedPipeClientStream"/> has no
    /// constructor that takes buffer sizes, and inherits whatever the server created.
    /// </remarks>
    public const int PipeBufferBytes = 4 * MaxMessageBytes;
}

public static class IpcMessageTypes
{
    public const string Request = "request";
    public const string Response = "response";
    public const string Event = "event";
}

public static class IpcCommands
{
    public const string GetStatus = "GetStatus";
    public const string Play = "Play";
    public const string Pause = "Pause";
    public const string Stop = "Stop";
    public const string Seek = "Seek";
    public const string SetVolume = "SetVolume";
}

/// <summary>State reported by the service; UI and protocol layers both project from this.</summary>
public sealed record RendererSnapshot
{
    public static RendererSnapshot Empty { get; } = new();

    public PlaybackStatus Playback { get; init; } = PlaybackStatus.Empty;

    public string? DeviceName { get; init; }

    /// <summary>True when SSDP announcements and the description endpoint are live.</summary>
    public bool DiscoveryActive { get; init; }

    public string? ControlUrl { get; init; }

    public string? LastError { get; init; }
}

/// <summary>
/// Single envelope for requests, responses and pushed events. Kept deliberately flat so the
/// wire format stays obvious and easy to inspect in logs.
/// </summary>
public sealed record IpcMessage
{
    public string Version { get; init; } = IpcProtocol.Version;

    public string Type { get; init; } = IpcMessageTypes.Request;

    public string? RequestId { get; init; }

    public string? Command { get; init; }

    public double? SeekSeconds { get; init; }

    public int? Volume { get; init; }

    public bool? Success { get; init; }

    public string? Error { get; init; }

    public RendererSnapshot? Snapshot { get; init; }
}

public static class IpcJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Length-prefixed JSON framing (4-byte little-endian length, then UTF-8 payload).</summary>
public static class IpcFraming
{
    /// <summary>
    /// Opens the client end of the service pipe.
    /// </summary>
    /// <remarks>
    /// The client cannot choose pipe buffer sizes (no such constructor exists), so the buffer is
    /// entirely determined by the server, which is why <see cref="IpcProtocol.PipeBufferBytes"/>
    /// is applied there. Use this helper instead of constructing a stream by hand so the pipe name
    /// and options stay in one place.
    /// </remarks>
    public static NamedPipeClientStream CreateClientStream(string? pipeName = null)
        => new(
            ".",
            pipeName ?? IpcProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

    public static async ValueTask WriteAsync(Stream stream, IpcMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options);
        if (payload.Length > IpcProtocol.MaxMessageBytes)
        {
            throw new InvalidOperationException("IPC message exceeds the size limit.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one message, or returns null on a clean end of stream.</summary>
    public static async ValueTask<IpcMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var read = await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (!read)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > IpcProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException($"Invalid IPC frame length {length}.");
        }

        var payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("IPC stream ended inside a frame.");
        }

        return JsonSerializer.Deserialize<IpcMessage>(payload, IpcJson.Options)
            ?? throw new InvalidDataException("IPC payload was not a message.");
    }

    private static async ValueTask<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return offset != 0
                    ? throw new EndOfStreamException("IPC stream ended inside a frame header.")
                    : false;
            }

            offset += read;
        }

        return true;
    }
}
