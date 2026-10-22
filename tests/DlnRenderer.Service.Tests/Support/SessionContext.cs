using DlnRenderer.Dlna;
using DlnRenderer.Player.Mpv;
using DlnRenderer.Service.Hosting;

namespace DlnRenderer.Service.Tests;

/// <summary>
/// A real libmpv player, a real overlay window and the session that drives them.
/// </summary>
/// <remarks>
/// Shared by the tests that exercise <see cref="RendererSession"/> end to end. Each instance writes
/// its own log file, so a failure can be read back without guessing which test produced which line.
/// </remarks>
internal sealed class SessionContext : IAsyncDisposable
{
    private readonly MpvPlayer _player;

    private SessionContext(MpvPlayer player, VideoPresenter presenter, RendererSession session, string logPath)
    {
        _player = player;
        Presenter = presenter;
        Session = session;
        LogPath = logPath;
    }

    public VideoPresenter Presenter { get; }

    public RendererSession Session { get; }

    public string LogPath { get; }

    public static async Task<SessionContext> CreateAsync()
    {
        var presenter = await VideoPresenter.StartAsync();
        var logPath = Path.Combine(
            Path.GetTempPath(),
            "dlnrenderer-session-tests",
            $"session-{Guid.NewGuid():N}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var gate = new object();

        var player = new MpvPlayer(new MpvPlayerOptions
        {
            VideoWindowHandle = presenter.ChildHandle,
            AudioOutput = "null",
            LogLevel = "info",
        });

        player.LogMessage += (_, message) =>
        {
            lock (gate)
            {
                File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} mpv {message.Level}: {message.Text.Trim()}{Environment.NewLine}");
            }
        };

        var session = new RendererSession(
            player,
            presenter,
            new DlnaOptions(),
            message =>
            {
                lock (gate)
                {
                    File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
                }
            });

        return new SessionContext(player, presenter, session, logPath);
    }

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        await _player.DisposeAsync();
        await Presenter.DisposeAsync();
    }
}
