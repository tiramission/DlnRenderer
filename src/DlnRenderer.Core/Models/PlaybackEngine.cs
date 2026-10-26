namespace DlnRenderer.Core.Models;

/// <summary>
/// The playback engine the service runs. Both engines implement <see cref="Core.Interfaces.IMediaPlayer"/>
/// and draw into the same video surface, so the choice is a service-start decision, not a code path
/// the rest of the system has to know about.
/// </summary>
public enum PlaybackEngine
{
    /// <summary>libmpv. The original engine, and the default.</summary>
    Mpv = 0,

    /// <summary>libVLC via LibVLCSharp. Added for devices or streams where mpv underperforms.</summary>
    Vlc = 1,
}

/// <summary>Names and descriptions for <see cref="PlaybackEngine"/>, shared by the UI and the log.</summary>
public static class PlaybackEngineInfo
{
    /// <summary>Stable identifier used in logs, settings diagnostics and the IPC snapshot.</summary>
    public static string Id(PlaybackEngine engine) => engine switch
    {
        PlaybackEngine.Mpv => "mpv",
        PlaybackEngine.Vlc => "vlc",
        _ => $"unknown({(int)engine})",
    };

    /// <summary>Human-readable name shown in the interface.</summary>
    public static string DisplayName(PlaybackEngine engine) => engine switch
    {
        PlaybackEngine.Mpv => "mpv（libmpv）",
        PlaybackEngine.Vlc => "VLC（libvlc）",
        _ => $"未知内核 ({(int)engine})",
    };

    /// <summary>Parses an id produced by <see cref="Id"/>; unknown ids fall back to mpv.</summary>
    public static PlaybackEngine FromId(string? id) => id switch
    {
        "vlc" => PlaybackEngine.Vlc,
        _ => PlaybackEngine.Mpv,
    };
}
