using DlnRenderer.Core.Models;

namespace DlnRenderer.Player.Mpv;

/// <summary>libmpv could not be loaded or initialised.</summary>
public sealed class MpvInitializationException : Exception
{
    public MpvInitializationException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>An mpv command or property write failed.</summary>
public sealed class MpvCommandException(string command, int errorCode, string errorText)
    : PlayerCommandException($"mpv command '{command}' failed ({errorCode}): {errorText}")
{
    public string Command { get; } = command;

    public int ErrorCode { get; } = errorCode;
}
