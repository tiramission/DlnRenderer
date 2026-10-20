using System.Globalization;

namespace DlnRenderer.Dlna.Upnp;

/// <summary>
/// UPnP time format: <c>H+:MM:SS[.f]</c>. It is used by AVTransport positions and by DIDL-Lite
/// <c>duration</c> attributes, whose optional day component is not handled by
/// <see cref="TimeSpan.TryParse(string, IFormatProvider, out TimeSpan)"/>.
/// </summary>
public static class UpnpTime
{
    public static bool TryParse(string? text, out TimeSpan position)
    {
        position = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        var hours = 0;
        if (parts.Length == 3
            && (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours) || hours < 0))
        {
            return false;
        }

        var minutesIndex = parts.Length == 3 ? 1 : 0;
        if (!int.TryParse(parts[minutesIndex], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || minutes is < 0 or > 59)
        {
            return false;
        }

        double seconds = 0;
        var secondsText = parts[^1];
        if (!double.TryParse(secondsText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds)
            || seconds is < 0 or >= 60)
        {
            return false;
        }

        position = TimeSpan.FromSeconds((hours * 3600) + (minutes * 60) + seconds);
        return true;
    }

    /// <summary>Formats a duration the way UPnP expects it: <c>HH:MM:SS</c>.</summary>
    public static string Format(TimeSpan value)
    {
        var hours = (int)value.TotalHours;
        return string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{value.Minutes:00}:{value.Seconds:00}");
    }
}
