using DlnRenderer.Player.Mpv;

namespace DlnRenderer.Service.Tests;

/// <summary>
/// A fact that runs only when the native libmpv DLL is present in the test output.
/// </summary>
/// <remarks>
/// libmpv is GPL-2.0-or-later and deliberately not vendored (docs/architecture.md §17), so a clean
/// machine - CI included - has no DLL. These tests drive the real player and assert what it
/// reports, so without the binary they would assert nothing: skipping is the honest outcome, and
/// the message names the file and the script that provides it so a missing binary never looks like
/// a pass. SessionWindowLifecycleTests used its own copy of this attribute; the two were kept
/// identical on purpose, and now there is one.
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
