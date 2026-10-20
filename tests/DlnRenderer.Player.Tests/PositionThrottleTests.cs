using DlnRenderer.Player.Mpv;

namespace DlnRenderer.Player.Tests;

public class PositionThrottleTests
{
    [Fact]
    public void First_sample_is_always_published()
    {
        var throttle = new PositionThrottle(TimeSpan.FromMilliseconds(250));

        Assert.True(throttle.ShouldPublish(TimeSpan.Zero));
    }

    [Fact]
    public void Samples_inside_the_interval_are_dropped()
    {
        var throttle = new PositionThrottle(TimeSpan.FromMilliseconds(250));
        Assert.True(throttle.ShouldPublish(TimeSpan.FromMilliseconds(0)));

        Assert.False(throttle.ShouldPublish(TimeSpan.FromMilliseconds(100)));
        Assert.False(throttle.ShouldPublish(TimeSpan.FromMilliseconds(249)));
        Assert.True(throttle.ShouldPublish(TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public void Reset_forces_the_next_sample_through()
    {
        var throttle = new PositionThrottle(TimeSpan.FromSeconds(1));
        Assert.True(throttle.ShouldPublish(TimeSpan.Zero));
        Assert.False(throttle.ShouldPublish(TimeSpan.FromMilliseconds(200)));

        throttle.Reset();

        Assert.True(throttle.ShouldPublish(TimeSpan.FromMilliseconds(210)));
    }
}
