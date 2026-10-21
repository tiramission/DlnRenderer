using DlnRenderer.Service.Hosting;

namespace DlnRenderer.Service.Tests;

/// <summary>
/// Rules for deciding whether this process is running as a Windows service.
/// </summary>
/// <remarks>
/// The decision controls whether status is reported to the Service Control Manager. Reporting when
/// there is no SCM is harmless, but failing to report when there is one makes the SCM declare the
/// start a failure (event 7009) while the renderer is in fact serving requests - so the rule has to
/// be right in both directions. The parent-image comparison is pure, so it is tested directly
/// instead of by spawning a real service.
/// </remarks>
public sealed class ServiceLaunchDetectorTests
{
    [Fact]
    public void Parent_is_the_service_control_manager()
    {
        Assert.True(ServiceLaunchDetector.IsServiceControlManager("services.exe"));
    }

    [Fact]
    public void Parent_is_not_the_service_control_manager()
    {
        Assert.False(ServiceLaunchDetector.IsServiceControlManager("explorer.exe"));
        Assert.False(ServiceLaunchDetector.IsServiceControlManager("DlnRenderer.exe"));
        Assert.False(ServiceLaunchDetector.IsServiceControlManager("cmd.exe"));
    }

    [Fact]
    public void Comparison_ignores_case()
    {
        // Windows reports image names in whatever case the caller recorded; the SCM does not
        // guarantee lower case, and a case-sensitive match would silently disable status reporting.
        Assert.True(ServiceLaunchDetector.IsServiceControlManager("Services.exe"));
        Assert.True(ServiceLaunchDetector.IsServiceControlManager("SERVICES.EXE"));
    }

    [Fact]
    public void Unknown_parent_is_not_treated_as_the_scm()
    {
        // Null means the parent could not be read. Guessing "service" here would make the UI-launched
        // path report status to a handle it does not own; guessing "not a service" only loses the
        // report in a case the --service flag already covers.
        Assert.False(ServiceLaunchDetector.IsServiceControlManager(null));
        Assert.False(ServiceLaunchDetector.IsServiceControlManager(string.Empty));
    }

    [Fact]
    public void Full_path_is_not_the_image_name()
    {
        // The detector compares against the file name. A path would be a different string and must
        // not match, otherwise a non-service parent sitting at that path would be misread.
        Assert.False(ServiceLaunchDetector.IsServiceControlManager(@"C:\Windows\System32\services.exe"));
    }
}
