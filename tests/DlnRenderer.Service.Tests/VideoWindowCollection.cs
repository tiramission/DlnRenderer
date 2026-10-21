using Xunit;

// Window-related tests share process-global state: the desktop's topmost band, the window message
// queue, and ffmpeg's disk usage. Running them side by side makes otherwise deterministic
// assertions flaky, so the collection is serialised explicitly.
[assembly: CollectionBehavior(DisableTestParallelization = false)]

namespace DlnRenderer.Service.Tests;

/// <summary>
/// Collection for everything that owns a real Win32 window.
/// </summary>
/// <remarks>
/// xUnit runs tests within one collection sequentially, and keeps the collection's classes from
/// running against each other. The video-window tests additionally depend on being the only ones
/// touching the topmost band at a time, which is a desktop-wide resource.
/// </remarks>
[CollectionDefinition("video-window", DisableParallelization = true)]
public class VideoWindowCollection
{
}
