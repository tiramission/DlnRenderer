using System.Globalization;
using DlnRenderer.Core.Models;
using DlnRenderer.Dlna.Metadata;
using DlnRenderer.Dlna.Soap;
using DlnRenderer.Dlna.Upnp;

namespace DlnRenderer.Dlna.AvTransport;

/// <summary>
/// Implements the AVTransport:1 actions this renderer actually supports. Every response is built
/// from a fresh player snapshot, so a SOAP reply never claims a state the player is not in.
/// </summary>
/// <remarks>
/// Transport commands are accepted leniently: <c>Play</c>, <c>Pause</c>, <c>Stop</c> and <c>Seek</c>
/// never fault because "no media is loaded" or "the target is past the end". Casting clients drive
/// them that way routinely - restoring a position before the URI arrives, re-issuing Play on a
/// resume, seeking into a stream whose duration is still unknown - and a device that answers
/// 701/601 to those makes the phone give up and report a failed cast. The state the renderer reports
/// afterwards still comes from the player, so nothing is faked: the command is simply a no-op when
/// there is nothing to act on.
/// </remarks>
public sealed class AvTransportService(ITransportRenderer renderer, Action<string> log)
{
    private const uint SupportedInstanceId = 0;

    public async Task<string> HandleAsync(SoapRequest request, CancellationToken cancellationToken)
    {
        var instanceId = ReadInstanceId(request);

        if (!string.IsNullOrEmpty(request.HeaderAction)
            && !string.Equals(request.HeaderAction, request.Action, StringComparison.Ordinal))
        {
            // Cosmetic mismatch (see SoapXml.Parse): the body wins, but it is worth a log line when
            // diagnosing a client that appears to send one action and get another's answer.
            log($"soap header/body action mismatch: header={request.HeaderAction} body={request.Action}");
        }

        switch (request.Action)
        {
            case "SetAVTransportURI":
                await SetAvTransportUriAsync(request, cancellationToken).ConfigureAwait(false);
                return Respond(request, []);

            case "Play":
                await PlayAsync(request, cancellationToken).ConfigureAwait(false);
                return Respond(request, []);

            case "Pause":
                await PauseAsync(cancellationToken).ConfigureAwait(false);
                return Respond(request, []);

            case "Stop":
                await InvokeRendererAsync(() => renderer.StopAsync(cancellationToken), "Stop").ConfigureAwait(false);
                return Respond(request, []);

            case "Seek":
                await SeekAsync(request, cancellationToken).ConfigureAwait(false);
                return Respond(request, []);

            // Single-track renderer: the queue commands are declared because clients read the SCPD
            // to decide what they may send, and answering them is a no-op rather than a fault.
            case "Next":
            case "Previous":
                log($"{request.Action} ignored: this renderer has a single-item queue");
                return Respond(request, []);

            case "SetPlayMode":
                log($"play mode '{request.Argument("NewPlayMode")}' accepted (single-item playback, no looping implemented)");
                return Respond(request, []);

            case "GetTransportInfo":
                return await GetTransportInfoAsync(request, cancellationToken).ConfigureAwait(false);

            case "GetPositionInfo":
                return await GetPositionInfoAsync(request, cancellationToken).ConfigureAwait(false);

            case "GetMediaInfo":
                return await GetMediaInfoAsync(request, cancellationToken).ConfigureAwait(false);

            case "GetDeviceCapabilities":
                return Respond(request,
                [
                    new("PlayMedia", "NETWORK,HDD"),
                    new("RecMedia", "NOT_IMPLEMENTED"),
                    new("RecQualityModes", "NOT_IMPLEMENTED"),
                ]);

            case "GetTransportSettings":
                return Respond(request,
                [
                    new("PlayMode", "NORMAL"),
                    new("RecQualityMode", "NOT_IMPLEMENTED"),
                ]);

            case "GetCurrentTransportActions":
                var snapshot = await renderer.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                return Respond(request, [new("Actions", CurrentActions(snapshot.State))]);

            default:
                _ = instanceId;
                throw new UpnpException(UpnpErrorCode.InvalidAction, $"unsupported action {request.Action}");
        }
    }

    private async Task SetAvTransportUriAsync(SoapRequest request, CancellationToken cancellationToken)
    {
        var rawUri = request.Argument("CurrentURI");
        if (string.IsNullOrWhiteSpace(rawUri))
        {
            throw new UpnpException(UpnpErrorCode.InvalidArgs, "CurrentURI is required");
        }

        if (!TryCreateMediaUri(rawUri.Trim(), out var uri))
        {
            throw new UpnpException(
                UpnpErrorCode.ArgumentValueInvalid,
                "CurrentURI must be an absolute http, https or file URI");
        }

        var metadata = request.Argument("CurrentURIMetaData");

        try
        {
            await renderer
                .LoadAsync(uri, string.IsNullOrWhiteSpace(metadata) ? null : metadata, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The URI is stored by the renderer before it touches the player, so the control point
            // can still query it and retry. Reporting the load failure as a SOAP fault would abort
            // the cast on the phone (the media server may simply not be ready yet); the failure is
            // surfaced to the local UI through the renderer's error state instead.
            log($"avtransport load failed for {SafeUri(uri)}: {ex.Message}");
        }

        log($"avtransport set uri {SafeUri(uri)}");
    }

    private async Task PlayAsync(SoapRequest request, CancellationToken cancellationToken)
    {
        var speed = request.Argument("Speed");

        // Only normal speed is implemented, but a rate other than "1" is not a reason to refuse:
        // clients send the value they last used for a different device. Log it and play at 1x.
        if (!string.IsNullOrWhiteSpace(speed) && speed.Trim() != "1")
        {
            log($"Play requested at speed '{speed.Trim()}'; playing at 1x");
        }

        if (renderer.CurrentUri is null)
        {
            log("Play ignored: no media has been set with SetAVTransportURI");
            return;
        }

        await InvokeRendererAsync(
            () => renderer.PlayAsync(cancellationToken),
            "Play").ConfigureAwait(false);
    }

    private async Task PauseAsync(CancellationToken cancellationToken)
    {
        if (renderer.CurrentUri is null)
        {
            log("Pause ignored: no media has been set with SetAVTransportURI");
            return;
        }

        await InvokeRendererAsync(() => renderer.PauseAsync(cancellationToken), "Pause").ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a renderer operation and maps an unexpected failure onto a UPnP fault. Without this a
    /// player error would escape as a 500 without a UPnP error code, which control points cannot
    /// interpret.
    /// </summary>
    private static async Task InvokeRendererAsync(Func<Task> action, string operation)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (UpnpException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new UpnpException(UpnpErrorCode.ActionFailed, $"{operation} failed: {ex.Message}");
        }
    }

    private async Task SeekAsync(SoapRequest request, CancellationToken cancellationToken)
    {
        var unit = request.Argument("Unit")?.Trim();

        // TRACK_NR addresses a position in a queue; there is exactly one track here, so any value is
        // the current one. Clients probe with it before deciding whether seek is available.
        if (string.Equals(unit, "TRACK_NR", StringComparison.OrdinalIgnoreCase))
        {
            log($"Seek by track number ignored (single-item queue): '{request.Argument("Target")}'");
            return;
        }

        if (!string.Equals(unit, "REL_TIME", StringComparison.Ordinal)
            && !string.Equals(unit, "ABS_TIME", StringComparison.Ordinal))
        {
            // An absent or unknown unit still carries a time target in practice; only a target that
            // cannot be parsed at all is a genuine protocol error.
            log($"Seek with unsupported unit '{unit}'; treating the target as an absolute time");
        }

        var target = request.Argument("Target");
        if (!TryParseTime(target, out var position))
        {
            throw new UpnpException(UpnpErrorCode.ArgumentValueInvalid, "Target must be H+:MM:SS");
        }

        if (renderer.CurrentUri is null)
        {
            log($"Seek to {target} ignored: no media has been set with SetAVTransportURI");
            return;
        }

        var status = await renderer.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        // Clamp instead of faulting. A duration reported a moment ago can be shorter than the
        // target for a live or still-loading stream, and clients seek to the very end routinely
        // (resume position, "jump to end" of a clip). Faulting 601 aborts the cast on the phone.
        if (status.Duration is { } duration && duration > TimeSpan.Zero && position > duration)
        {
            log($"Seek target {target} is past the duration {FormatTime(duration)}; clamping");
            position = duration;
        }

        try
        {
            await InvokeRendererAsync(() => renderer.SeekAsync(position, cancellationToken), "Seek")
                .ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new UpnpException(UpnpErrorCode.ArgumentValueOutOfRange, "Target is not a valid position");
        }
    }

    private async Task<string> GetTransportInfoAsync(SoapRequest request, CancellationToken cancellationToken)
    {
        var snapshot = await renderer.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return Respond(request,
        [
            new("CurrentTransportState", MapState(snapshot.State)),
            new("CurrentTransportStatus", string.IsNullOrEmpty(snapshot.Error) ? "OK" : "ERROR_OCCURRED"),
            new("CurrentSpeed", "1"),
        ]);
    }

    private async Task<string> GetPositionInfoAsync(SoapRequest request, CancellationToken cancellationToken)
    {
        var snapshot = await renderer.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var hasMedia = renderer.CurrentUri is not null;

        return Respond(request,
        [
            new("Track", hasMedia ? "1" : "0"),
            new("TrackDuration", FormatDuration(snapshot.Duration)),
            new("TrackMetaData", FormatMetadata(renderer.CurrentMetadata)),
            new("TrackURI", renderer.CurrentUri?.AbsoluteUri ?? string.Empty),
            new("RelTime", FormatPosition(snapshot)),
            new("AbsTime", FormatPosition(snapshot)),
            new("RelCount", "2147483647"),
            new("AbsCount", "2147483647"),
        ]);
    }

    private async Task<string> GetMediaInfoAsync(SoapRequest request, CancellationToken cancellationToken)
    {
        var snapshot = await renderer.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var hasMedia = renderer.CurrentUri is not null;

        return Respond(request,
        [
            new("NrTracks", hasMedia ? "1" : "0"),
            new("MediaDuration", FormatDuration(snapshot.Duration)),
            new("CurrentURI", renderer.CurrentUri?.AbsoluteUri ?? string.Empty),
            new("CurrentURIMetaData", FormatMetadata(renderer.CurrentMetadata)),
            new("NextURI", string.Empty),
            new("NextURIMetaData", string.Empty),
            new("PlayMedium", hasMedia ? "NETWORK" : "NONE"),
            new("RecordMedium", "NOT_IMPLEMENTED"),
            new("WriteStatus", "NOT_IMPLEMENTED"),
        ]);
    }

    private uint ReadInstanceId(SoapRequest request)
    {
        var raw = request.Argument("InstanceID");
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new UpnpException(UpnpErrorCode.InvalidArgs, "InstanceID is required");
        }

        if (!uint.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var instanceId))
        {
            throw new UpnpException(UpnpErrorCode.InvalidArgs, "InstanceID must be an unsigned integer");
        }

        if (instanceId != SupportedInstanceId)
        {
            throw new UpnpException(UpnpErrorCode.InvalidInstanceId, $"only InstanceID {SupportedInstanceId} exists");
        }

        return instanceId;
    }

    private static string Respond(SoapRequest request, IReadOnlyList<KeyValuePair<string, string>> arguments)
        => SoapXml.BuildResponse(SoapNamespaces.AvTransportServiceType, request.Action, arguments);

    internal static string MapState(PlaybackState state) => state switch
    {
        PlaybackState.Playing => "PLAYING",
        PlaybackState.Paused => "PAUSED_PLAYBACK",
        PlaybackState.Buffering => "TRANSITIONING",
        PlaybackState.Transitioning => "TRANSITIONING",
        // Error and Stopped both have no transport in motion; status carries the error detail.
        _ => "STOPPED",
    };

    internal static string FormatDuration(TimeSpan? duration)
        => duration is { } value && value > TimeSpan.Zero
            ? FormatTime(value)
            : "00:00:00";

    internal static string FormatPosition(TransportSnapshot snapshot)
        => snapshot.State is PlaybackState.Stopped or PlaybackState.Error
            ? "00:00:00"
            : FormatTime(snapshot.Position);

    /// <summary>AVTransport times are H+:MM:SS, optionally with fractions.</summary>
    internal static string FormatTime(TimeSpan value) => UpnpTime.Format(value);

    internal static bool TryParseTime(string? text, out TimeSpan position) => UpnpTime.TryParse(text, out position);

    private static string FormatMetadata(string? metadata)
        => string.IsNullOrWhiteSpace(metadata) ? string.Empty : metadata;

    private static string CurrentActions(PlaybackState state) => state switch
    {
        PlaybackState.Playing => "Pause,Stop,Seek",
        PlaybackState.Paused => "Play,Stop,Seek",
        PlaybackState.Transitioning or PlaybackState.Buffering => "Stop",
        _ => "Play,Stop,Seek",
    };

    /// <summary>
    /// Only absolute http(s) URIs are accepted. A DLNA control endpoint must not become a generic
    /// path to arbitrary schemes (lavfi:, edl:, ...) nor a reader for local files (file: and bare
    /// Windows paths both resolve to the file scheme).
    /// </summary>
    internal static bool TryCreateMediaUri(string text, out Uri uri)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
        {
            uri = null!;
            return false;
        }

        if (parsed.Scheme is not ("http" or "https"))
        {
            uri = null!;
            return false;
        }

        // Control points ship credentials in the URL; never echo them into logs.
        uri = parsed;
        return true;
    }

    private static string SafeUri(Uri uri)
        => uri.UserInfo.Length > 0
            ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.SafeUnescaped)
            : uri.ToString();
}
