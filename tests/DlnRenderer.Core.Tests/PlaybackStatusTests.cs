using DlnRenderer.Core.Models;

namespace DlnRenderer.Core.Tests;

public class PlaybackStatusTests
{
    [Fact]
    public void Empty_status_is_stopped_with_sane_defaults()
    {
        var status = PlaybackStatus.Empty;

        Assert.Equal(PlaybackState.Stopped, status.State);
        Assert.Null(status.Media);
        Assert.Null(status.ErrorMessage);
        Assert.Equal(TimeSpan.Zero, status.Position);
        Assert.Equal(100, status.Volume);
        Assert.False(status.Muted);
    }

    [Fact]
    public void With_expressions_do_not_mutate_the_shared_empty_instance()
    {
        var playing = PlaybackStatus.Empty with { State = PlaybackState.Playing, Volume = 40 };

        Assert.Equal(PlaybackState.Playing, playing.State);
        Assert.Equal(40, playing.Volume);
        Assert.Equal(PlaybackState.Stopped, PlaybackStatus.Empty.State);
        Assert.Equal(100, PlaybackStatus.Empty.Volume);
    }

    [Fact]
    public void Media_info_requires_a_uri_and_keeps_optional_metadata_null()
    {
        var uri = new Uri("http://192.168.1.10:8200/movie.mp4");
        var media = new MediaInfo(uri);

        Assert.Equal(uri, media.MediaUri);
        Assert.Null(media.Title);
        Assert.Null(media.MimeType);
        Assert.Null(media.Duration);
    }
}
