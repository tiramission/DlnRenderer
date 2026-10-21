using System.Diagnostics;
using System.Globalization;

namespace DlnRenderer.Service.Hosting;

/// <summary>
/// Minimal append-only log shared by the DLNA host, the player adapter and the IPC server.
/// The service is a console app so the same lines go to stdout and to a dated file.
/// </summary>
public sealed class ServiceLog : IDisposable
{
    /// <summary>How many days of log files to keep; older ones are deleted.</summary>
    /// <remarks>Internal so tests can assert against the real value instead of a copy.</remarks>
    internal const int RetentionDays = 7;

    private readonly object _gate = new();
    private readonly StreamWriter? _writer;
    private readonly string? _directory;
    private DateTime _lastRetentionDay = DateTime.Now.Date;
    private bool _disposed;

    public ServiceLog(string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // A service that runs for weeks writes one file per day and never removes any of them,
            // so an unattended machine slowly fills the disk with logs nobody reads.
            _directory = directory;
            DeleteExpiredFiles(filePath);

            _writer = new StreamWriter(
                new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read),
                new System.Text.UTF8Encoding(false))
            {
                AutoFlush = true,
            };

            FilePath = filePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not open log file '{filePath}': {ex.Message}");
        }
    }

    public string? FilePath { get; }

    public void Write(string message)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Console.WriteLine(line);
            try
            {
                _writer?.WriteLine(line);
            }
            catch (IOException)
            {
                // Losing a log line must never take the renderer down.
            }

            // Files are named per day; a long-running process therefore has to prune on its own
            // rather than only at startup.
            var today = DateTime.Now.Date;
            if (today != _lastRetentionDay)
            {
                _lastRetentionDay = today;
                DeleteExpiredFiles(FilePath);
            }
        }
    }

    /// <summary>
    /// Deletes log files older than <see cref="RetentionDays"/>, keeping the current one.
    /// </summary>
    /// <remarks>
    /// Only files this class produced are considered: the name must match the
    /// <c>service-yyyyMMdd.log</c> pattern, so pointing the log at a directory shared with other
    /// tools cannot cause it to delete their files. Failures are ignored on purpose - retention is
    /// housekeeping, and a locked or permission-denied file must not affect logging or the renderer.
    /// </remarks>
    internal void DeleteExpiredFiles(string? currentFile)
    {
        if (string.IsNullOrEmpty(_directory) || !Directory.Exists(_directory))
        {
            return;
        }

        var cutoff = DateTime.Now.Date.AddDays(-RetentionDays);

        foreach (var file in Directory.EnumerateFiles(_directory, "service-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!name.StartsWith("service-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!DateTime.TryParseExact(
                    name["service-".Length..],
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var written))
            {
                continue;
            }

            if (written >= cutoff)
            {
                continue;
            }

            if (currentFile is not null
                && string.Equals(Path.GetFullPath(file), Path.GetFullPath(currentFile), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Picked up again on the next sweep.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Dispose();
        }
    }
}

public static class ServicePaths
{
    /// <summary>
    /// Root for state that must survive restarts: the device UUID and the logs.
    /// </summary>
    /// <remarks>
    /// The location depends on which account the process runs under, and the difference is not
    /// cosmetic. Under an interactive user, <c>%LOCALAPPDATA%\DlnRenderer</c> is right: it is
    /// per-user, needs no elevation, and the settings page writes next to it. Under a service
    /// account, <c>LocalApplicationData</c> resolves into the service profile, which is not where
    /// the user's settings live and is wiped when the package is repaired. Those runs need the
    /// machine-wide <c>%ProgramData%\DlnRenderer</c> instead. (The shipped package registers no
    /// service; the branch exists for a service registered by hand with <c>sc create</c>.)
    /// </remarks>
    public static string DataDirectory
    {
        get
        {
            if (IsServiceAccount)
            {
                var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (!string.IsNullOrWhiteSpace(programData))
                {
                    return Path.Combine(programData, "DlnRenderer");
                }
            }

            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "DlnRenderer");
        }
    }

    /// <summary>
    /// True when this process runs as a Windows service under a machine account. Detected from the
    /// session rather than from a flag, so an accidental interactive run of the service binary still
    /// writes to the user profile.
    /// </summary>
    public static bool IsServiceAccount
    {
        get
        {
            // Session 0 is where services live; an interactive desktop session is never 0. This is
            // the same signal the OS uses, and it costs no privileges to read.
            try
            {
                return Process.GetCurrentProcess().SessionId == 0;
            }
            catch (Exception)
            {
                // If the query fails, assume the safer layout: writing to a service profile
                // would lose the data, writing to ProgramData merely needs an ACL.
                return false;
            }
        }
    }

    public static string DeviceStateFile => Path.Combine(DataDirectory, "device.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string NewLogFile()
        => Path.Combine(LogDirectory, $"service-{DateTime.Now:yyyyMMdd}.log");
}
