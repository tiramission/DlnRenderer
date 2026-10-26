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

    /// <summary>
    /// Wire-protocol version. Bumped whenever a message field changes meaning.
    /// </summary>
    /// <remarks>
    /// The two executables are separate processes, and a service built from older source simply
    /// ignores fields it does not know: a request that says "suspend the picture" lands as "no host",
    /// which the old service answers with a fullscreen overlay. That is not a bug to debug twice -
    /// it is a stale binary, so the mismatch is refused loudly instead.
    /// </remarks>
    public const string Version = "1.1";

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
    public const string SetMute = "SetMute";

    /// <summary>
    /// Tells the service where it may draw video: the display mode, the UI window to embed into and
    /// the reserved rectangle. Sent on connect, whenever that rectangle changes, and when the user
    /// switches mode.
    /// </summary>
    public const string SetVideoTarget = "SetVideoTarget";

    /// <summary>Stops the service process. Only sent when the user exits the whole application.</summary>
    public const string Shutdown = "Shutdown";
}

/// <summary>
/// Where the service may present video, as reported by the UI.
/// </summary>
/// <param name="Mode">Requested presentation mode.</param>
/// <param name="HostWindow">UI window handle to embed into; 0 means "no host available".</param>
/// <param name="X">Reserved area's left edge, in physical pixels, relative to the host's client area.</param>
/// <param name="Y">Reserved area's top edge, same coordinate space.</param>
/// <param name="Width">Reserved area width in physical pixels; 0 or less means "no area right now".</param>
/// <param name="Height">Reserved area height in physical pixels.</param>
/// <remarks>
/// Coordinates are client-relative and already scaled to physical pixels by the UI, because only the
/// UI knows its DPI and layout. Keeping the window position out of the contract means the service
/// never has to translate screen coordinates when the user drags the window.
/// </remarks>
public sealed record VideoTarget
{
    /// <summary>Embedded but with nothing usable: the UI connects before it has laid out video.</summary>
    public static VideoTarget Default { get; } = new();

    public DisplayMode Mode { get; init; } = DisplayMode.Fullscreen;

    public long HostWindow { get; init; }

    public int X { get; init; }

    public int Y { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>
    /// False while the UI has nowhere to show the picture - the player page is not on screen.
    /// </summary>
    /// <remarks>
    /// Distinct from "no host". Embedded mode never falls back to the fullscreen overlay, so both
    /// states now hide the picture - the difference is what happens when the UI comes back: a
    /// suspended target is a deliberate pause that resumes on navigation, a host-less one is a target
    /// the UI has not filled in yet. Either way the cast keeps running and the audio never stops,
    /// which is the point: navigating away must not throw a film onto the whole screen.
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// True when there is a window to embed into.
    /// </summary>
    public bool HasHost => HostWindow != 0;

    /// <summary>True when the reserved area is large enough to draw into.</summary>
    public bool HasArea => Width > 1 && Height > 1;

    /// <summary>
    /// Corner radius for the embedded picture, in physical pixels. 0 leaves it square (fullscreen).
    /// </summary>
    /// <remarks>
    /// The UI sends this because it is the only side that knows both the design radius and the DPI
    /// scale. The service rounds its own window, so the video is clipped to the same shape the page
    /// would otherwise draw - a square picture inside a rounded layout reads as a hole punched in
    /// the window.
    /// </remarks>
    public int CornerRadius { get; init; }

    /// <summary>True when this target can actually be honoured in embedded mode.</summary>
    public bool CanEmbed => Enabled && Mode == DisplayMode.Embedded && HasHost && HasArea;
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

    /// <summary>
    /// Playback engine the service is running, as a stable id ("mpv" / "vlc"), or null when the
    /// service predates the field. The UI shows it as a badge; a null is rendered as "no badge"
    /// rather than assuming mpv.
    /// </summary>
    public string? Engine { get; init; }

    /// <summary>
    /// Build of the service that produced this snapshot, or null when the service predates the field.
    /// </summary>
    /// <remarks>
    /// The UI and the service are separate executables, and a stale service silently ignores message
    /// fields it does not know - a fixed bug then looks unfixed, or worse, behaves differently from
    /// what the code says. Reporting the build lets the UI say "this service is old" instead of
    /// leaving both sides to guess.
    /// </remarks>
    public string? ServiceBuild { get; init; }
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

    public bool? Muted { get; init; }

    /// <summary>Video target carried by <see cref="IpcCommands.SetVideoTarget"/>.</summary>
    public VideoTarget? VideoTarget { get; init; }

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
