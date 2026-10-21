namespace DlnRenderer.Core.Settings;

/// <summary>
/// User-facing renderer settings, persisted as JSON under <c>%LOCALAPPDATA%\DlnRenderer</c>.
/// </summary>
/// <remarks>
/// Deliberately small: every field is one the user has a real reason to change, and every field has
/// a usable default so a missing or unreadable file cannot stop the renderer from starting.
/// Validation lives here rather than in the UI so the service applies exactly the same rules as the
/// settings page.
/// </remarks>
public sealed record RendererSettings
{
    public const string DefaultDeviceName = "DlnRenderer";

    /// <summary>Long enough for a room name, short enough for a control point's device list.</summary>
    public const int MaxDeviceNameLength = 64;

    /// <summary>Name advertised over SSDP and shown in a control point's device list.</summary>
    public string DeviceName { get; init; } = DefaultDeviceName;

    /// <summary>Register the UI in the current user's Run key.</summary>
    public bool StartWithWindows { get; init; }

    /// <summary>Closing the window hides it to the tray instead of exiting the UI.</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>Passed to libmpv as <c>hwdec=auto-safe</c>; disabling forces software decoding.</summary>
    public bool HardwareDecoding { get; init; } = true;

    public static RendererSettings Default { get; } = new();

    /// <summary>Returns a copy with the device name trimmed and no other change.</summary>
    public RendererSettings Normalize() => this with { DeviceName = (DeviceName ?? string.Empty).Trim() };

    /// <summary>
    /// Validates the settings and explains the first problem.
    /// </summary>
    /// <remarks>
    /// Rejecting rather than silently truncating is the point: a 200-character device name would be
    /// advertised to every control point, and a name made of control characters breaks the XML
    /// description. The caller is expected to show <paramref name="error"/> to the user.
    /// </remarks>
    public static bool TryValidate(RendererSettings settings, out string? error)
    {
        error = null;

        var name = settings.DeviceName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "设备名称不能为空。";
            return false;
        }

        // Leading/trailing whitespace is trimmed rather than rejected: pasting a name with a
        // trailing space is common and the trimmed result is what the user meant.
        if (name.Trim().Length > MaxDeviceNameLength)
        {
            error = $"设备名称不能超过 {MaxDeviceNameLength} 个字符。";
            return false;
        }

        foreach (var character in name)
        {
            if (char.IsControl(character))
            {
                error = "设备名称不能包含控制字符。";
                return false;
            }
        }

        return true;
    }
}
