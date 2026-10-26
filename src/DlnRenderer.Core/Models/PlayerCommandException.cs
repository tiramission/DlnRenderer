namespace DlnRenderer.Core.Models;

/// <summary>
/// A playback engine refused or could not execute a command. Engine-specific exceptions derive from
/// this so the session layer can react to "the player said no" without knowing which engine is running.
/// </summary>
/// <remarks>
/// The concrete case is a seek on a player that just became idle: the session falls back to a
/// deferred reload instead of surfacing a failure the control point cannot act on.
/// </remarks>
public class PlayerCommandException : InvalidOperationException
{
    public PlayerCommandException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
