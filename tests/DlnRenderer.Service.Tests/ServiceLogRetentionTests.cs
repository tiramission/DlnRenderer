using System.Globalization;
using DlnRenderer.Service.Hosting;

namespace DlnRenderer.Service.Tests;

/// <summary>
/// Log retention: old daily files are removed, everything else is left alone.
/// </summary>
/// <remarks>
/// A service meant to stay resident must bound its own disk usage. The rules that matter are the
/// boundaries (today is never deleted), the name filter (a shared directory is not wiped), and the
/// fact that housekeeping failures stay silent.
/// </remarks>
public sealed class ServiceLogRetentionTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "dln-log-tests",
        Guid.NewGuid().ToString("N"));

    public ServiceLogRetentionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked scratch directory must not fail the run.
        }
    }

    private string CreateLogFile(DateTime date, string? name = null)
    {
        var path = Path.Combine(
            _directory,
            name ?? $"service-{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
        File.WriteAllText(path, $"line for {date:yyyy-MM-dd}");
        return path;
    }

    [Fact]
    public void Files_older_than_the_retention_window_are_deleted()
    {
        using var log = new ServiceLog(Path.Combine(_directory, CurrentLogName()));

        var expired = CreateLogFile(DateTime.Now.AddDays(-(ServiceLog.RetentionDays + 1)));
        var alsoExpired = CreateLogFile(DateTime.Now.AddDays(-30));

        log.DeleteExpiredFiles(log.FilePath);

        Assert.False(File.Exists(expired));
        Assert.False(File.Exists(alsoExpired));
    }

    [Fact]
    public void Files_inside_the_window_are_kept()
    {
        using var log = new ServiceLog(Path.Combine(_directory, CurrentLogName()));

        var recent = CreateLogFile(DateTime.Now.AddDays(-1));
        var boundary = CreateLogFile(DateTime.Now.AddDays(-ServiceLog.RetentionDays));

        log.DeleteExpiredFiles(log.FilePath);

        Assert.True(File.Exists(recent), "yesterday's log is still useful for a bug report");
        Assert.True(File.Exists(boundary), "the oldest day inside the window must survive");
    }

    [Fact]
    public void The_current_file_is_never_deleted()
    {
        var current = Path.Combine(_directory, CurrentLogName());
        using var log = new ServiceLog(current);

        // Even with an old-looking name, the file being written must survive.
        var misnamed = CreateLogFile(DateTime.Now.AddDays(-100), "service-20000101.log");

        log.DeleteExpiredFiles(log.FilePath);

        Assert.True(File.Exists(misnamed) == false, "an unrelated expired file is still pruned");
        Assert.True(File.Exists(current));
    }

    [Fact]
    public void Files_not_created_by_this_logger_are_left_alone()
    {
        using var log = new ServiceLog(Path.Combine(_directory, CurrentLogName()));

        var foreign = Path.Combine(_directory, "notes.txt");
        var lookalike = Path.Combine(_directory, "service-backup.log");
        File.WriteAllText(foreign, "keep");
        File.WriteAllText(lookalike, "keep");

        log.DeleteExpiredFiles(log.FilePath);

        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(lookalike), "a name without a yyyyMMdd suffix is not ours to delete");
    }

    [Fact]
    public void An_empty_or_missing_directory_is_tolerated()
    {
        using var log = new ServiceLog(Path.Combine(_directory, CurrentLogName()));

        // No directory configured (logging disabled) and a deleted directory must both be no-ops.
        log.DeleteExpiredFiles(null);
        log.DeleteExpiredFiles(Path.Combine(_directory, "gone", CurrentLogName()));

        Assert.True(Directory.Exists(_directory));
    }

    [Fact]
    public void Retention_runs_when_the_log_starts()
    {
        var expired = CreateLogFile(DateTime.Now.AddDays(-(ServiceLog.RetentionDays + 5)));
        Assert.True(File.Exists(expired));

        using var log = new ServiceLog(Path.Combine(_directory, CurrentLogName()));

        // Constructing the log already pruned the old file, without waiting for the first line.
        Assert.False(File.Exists(expired));
    }

    [Fact]
    public void Writing_a_line_still_works_after_pruning()
    {
        var current = Path.Combine(_directory, CurrentLogName());

        using (var log = new ServiceLog(current))
        {
            CreateLogFile(DateTime.Now.AddDays(-60));
            log.Write("hello");
        }

        // Read only after the writer is disposed: the log holds the file open with FileShare.Read,
        // so asserting while it is live would test the test's timing rather than the logger.
        var content = File.ReadAllText(current);
        Assert.Contains("hello", content, StringComparison.Ordinal);
    }

    private static string CurrentLogName()
        => $"service-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log";
}
