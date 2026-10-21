using System.Net;

namespace DlnRenderer.Dlna.Ssdp;

/// <summary>The difference between the interfaces currently present and the ones joined.</summary>
/// <param name="Added">Interfaces that exist now but are not joined yet.</param>
/// <param name="Removed">Joined interfaces that no longer exist.</param>
public readonly record struct MembershipDelta(
    IReadOnlyList<IPAddress> Added,
    IReadOnlyList<IPAddress> Removed)
{
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0;

    /// <summary>
    /// Computes what must be joined and left so that <paramref name="joined"/> matches
    /// <paramref name="current"/>.
    /// </summary>
    /// <remarks>
    /// Pure by design: the multicast membership rules are the part worth testing, and exercising them
    /// against real adapters is not possible on a build agent or without administrator rights. The
    /// socket calls that consume this result stay in <see cref="SsdpServer"/>.
    /// </remarks>
    public static MembershipDelta Compute(
        IReadOnlyCollection<IPAddress> joined,
        IReadOnlyList<IPAddress> current)
    {
        var added = current.Where(address => !joined.Contains(address)).ToList();
        var removed = joined.Where(address => !current.Contains(address)).ToList();

        return new MembershipDelta(added, removed);
    }
}