namespace DlnRenderer.Player.Vlc;

/// <summary>libvlc could not be loaded or initialised.</summary>
public sealed class VlcInitializationException : Exception
{
    public VlcInitializationException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>A libvlc command failed.</summary>
public sealed class VlcCommandException(string command, string reason)
    : Core.Models.PlayerCommandException($"vlc command '{command}' failed: {reason}")
{
    public string Command { get; } = command;
}
