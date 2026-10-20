using System.Globalization;

namespace DlnRenderer.Service.Hosting;

/// <summary>
/// Minimal append-only log shared by the DLNA host, the player adapter and the IPC server.
/// The service is a console app so the same lines go to stdout and to a dated file.
/// </summary>
public sealed class ServiceLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter? _writer;
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
    public static string DataDirectory
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "DlnRenderer");
        }
    }

    public static string DeviceStateFile => Path.Combine(DataDirectory, "device.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string NewLogFile()
        => Path.Combine(LogDirectory, $"service-{DateTime.Now:yyyyMMdd}.log");
}
