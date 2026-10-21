using System.Globalization;
using System.Xml.Linq;
using DlnRenderer.Dlna.AvTransport;
using DlnRenderer.Dlna.Soap;

namespace DlnRenderer.Dlna.RenderingControl;

/// <summary>
/// RenderingControl:1 with the volume and mute actions control points use to verify a renderer
/// and to drive its volume.
/// </summary>
/// <remarks>
/// Volume maps to the <em>player's</em> volume, never to the Windows master volume: changing the
/// system mixer on behalf of a remote control point would affect every other application.
/// Supports the single virtual instance the renderer exposes (InstanceID 0) and the Master channel.
/// </remarks>
public sealed class RenderingControlService(ITransportRenderer renderer, Action<string> log)
{
    private const uint SupportedInstanceId = 0;
    private const string MasterChannel = "Master";

    public async Task<string> HandleAsync(SoapRequest request, CancellationToken cancellationToken)
    {
        RequireInstanceId(request);

        switch (request.Action)
        {
            case "GetVolume":
                RequireMasterChannel(request, "GetVolume");
                var volume = await renderer.GetVolumeAsync(cancellationToken).ConfigureAwait(false);
                return Respond(request,
                [
                    Pair("CurrentVolume", Clamp(volume).ToString(CultureInfo.InvariantCulture)),
                ]);

            case "SetVolume":
                RequireMasterChannel(request, "SetVolume");
                var desired = ParseVolume(request.Argument("DesiredVolume"));
                await SetVolumeAsync(desired, cancellationToken).ConfigureAwait(false);
                return Respond(request, []);

            case "GetMute":
                RequireMasterChannel(request, "GetMute");
                var muted = await renderer.GetMuteAsync(cancellationToken).ConfigureAwait(false);
                return Respond(request,
                [
                    Pair("CurrentMute", muted ? "1" : "0"),
                ]);

            case "SetMute":
                RequireMasterChannel(request, "SetMute");
                var mute = ParseMute(request.Argument("DesiredMute"));
                await SetMuteAsync(mute, cancellationToken).ConfigureAwait(false);
                return Respond(request, []);

            case "ListPresets":
                return Respond(request,
                [
                    Pair("CurrentPresetNameList", "FactoryDefaults"),
                ]);

            case "SelectPreset":
                // Only FactoryDefaults exists; selecting it is a no-op rather than a fake success
                // for arbitrary presets.
                var preset = request.Argument("PresetName")?.Trim();
                if (!string.Equals(preset, "FactoryDefaults", StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpnpException(UpnpErrorCode.RenderingInvalidName, $"preset '{preset}' does not exist");
                }

                return Respond(request, []);

            default:
                throw new UpnpException(UpnpErrorCode.InvalidAction, $"action '{request.Action}' is not supported");
        }
    }

    private async Task SetVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        try
        {
            await renderer.SetVolumeAsync(volume, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UpnpException)
        {
            throw new UpnpException(UpnpErrorCode.ActionFailed, $"SetVolume failed: {ex.Message}");
        }
    }

    private async Task SetMuteAsync(bool muted, CancellationToken cancellationToken)
    {
        try
        {
            await renderer.SetMuteAsync(muted, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UpnpException)
        {
            throw new UpnpException(UpnpErrorCode.ActionFailed, $"SetMute failed: {ex.Message}");
        }
    }

    private static int Clamp(int volume) => Math.Clamp(volume, 0, 100);

    private static int ParseVolume(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var volume))
        {
            throw UpnpException.InvalidArgs("DesiredVolume is required and must be an integer");
        }

        if (volume is < 0 or > 100)
        {
            throw new UpnpException(UpnpErrorCode.ArgumentValueOutOfRange, "DesiredVolume must be between 0 and 100");
        }

        return volume;
    }

    private static bool ParseMute(string? text)
    {
        var value = text?.Trim();

        // UPnP boolean state variables are exactly "0"/"1" or "false"/"true". Accepting looser forms
        // ("yes", "no") would silently invent a value the client never sent.
        return value?.ToLowerInvariant() switch
        {
            "1" or "true" => true,
            "0" or "false" => false,
            _ => throw UpnpException.InvalidArgs("DesiredMute is required and must be a boolean"),
        };
    }

    private static void RequireInstanceId(SoapRequest request)
    {
        var raw = request.Argument("InstanceID");
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw UpnpException.InvalidArgs("InstanceID is required");
        }

        if (!uint.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var instanceId))
        {
            throw UpnpException.InvalidArgs("InstanceID must be an unsigned integer");
        }

        if (instanceId != SupportedInstanceId)
        {
            throw new UpnpException(UpnpErrorCode.InvalidInstanceId, $"only InstanceID {SupportedInstanceId} exists");
        }
    }

    private static void RequireMasterChannel(SoapRequest request, string action)
    {
        var channel = request.Argument("Channel")?.Trim();

        if (string.IsNullOrEmpty(channel))
        {
            // Channel is optional in the spec and defaults to Master.
            return;
        }

        if (!string.Equals(channel, MasterChannel, StringComparison.Ordinal))
        {
            throw new UpnpException(UpnpErrorCode.RenderingInvalidChannel, $"{action} only supports the Master channel");
        }
    }

    private static string Respond(SoapRequest request, IReadOnlyList<KeyValuePair<string, string>> arguments)
        => SoapXml.BuildResponse(SoapNamespaces.RenderingControlServiceType, request.Action, arguments);

    private static KeyValuePair<string, string> Pair(string name, string value) => new(name, value);

    private void Log(string message) => log(message);
}
