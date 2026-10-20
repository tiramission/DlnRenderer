namespace DlnRenderer;

/// <summary>
/// Minimal diagnostics log used during playback verification. Writes only when the
/// <c>DLNRENDERER_LOG_PATH</c> environment variable points at a file, so normal runs stay silent.
/// </summary>
internal static class DiagnosticsLog
{
    private static readonly object Gate = new();
    private static string? _logPath = Environment.GetEnvironmentVariable("DLNRENDERER_LOG_PATH");

    /// <summary>Redirects the log file, e.g. from the <c>--log</c> command line switch.</summary>
    public static void Configure(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _logPath = path;
        }
    }

    public static void Write(string message)
    {
        var logPath = _logPath;
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    logPath,
                    $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never break playback.
        }
    }
}
