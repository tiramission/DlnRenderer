using DlnRenderer.Player.Mpv;

namespace DlnRenderer.Service.Tests;

/// <summary>
/// A fact that runs only when the native libmpv DLL is present in the test output.
/// </summary>
/// <remarks>
/// libmpv is GPL-2.0-or-later and deliberately not vendored (docs/architecture.md §17), so a
/// machine that has not run scripts/fetch-libmpv.ps1 has no DLL. These tests drive the real player
/// and assert what it reports, so without the binary they would assert nothing: skipping is the
/// honest outcome, and the message names the file and the script that provides it so a missing
/// binary never looks like a pass. CI runs the script, so these execute there rather than skip;
/// the skip is the fallback for a fresh checkout. SessionWindowLifecycleTests used its own copy of
/// this attribute; the two were kept identical on purpose, and now there is one.
/// </remarks>
public sealed class MpvFactAttribute : FactAttribute
{
    public MpvFactAttribute()
    {
        if (!MpvRuntime.IsAvailable())
        {
            Skip = $"libmpv-2.dll not found at {MpvRuntime.ExpectedPath}. Run scripts/fetch-libmpv.ps1.";
        }
    }
}

/// <summary>
/// A fact that needs ffmpeg as well, because the test generates its own media clips with it.
/// </summary>
/// <remarks>
/// xunit can only skip at discovery, so the ffmpeg check lives in an attribute rather than in the
/// test body. Before this existed the lifecycle tests said "skipped when ffmpeg is missing" in a
/// comment but actually threw from Process.Start, which is how a runner without ffmpeg turned
/// seven tests red instead of skipping them.
/// </remarks>
public sealed class MpvFFmpegFactAttribute : FactAttribute
{
    public MpvFFmpegFactAttribute()
    {
        if (!MpvRuntime.IsAvailable())
        {
            Skip = $"libmpv-2.dll not found at {MpvRuntime.ExpectedPath}. Run scripts/fetch-libmpv.ps1.";
            return;
        }

        if (!HasFfmpeg())
        {
            Skip = "ffmpeg not found on PATH; these tests generate their clips with it. Install ffmpeg to run them.";
        }
    }

    /// <summary>Looks for ffmpeg.exe on PATH, the same way the test would start it.</summary>
    private static bool HasFfmpeg()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory.Trim(), "ffmpeg.exe")));
    }
}
