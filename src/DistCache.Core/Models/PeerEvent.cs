namespace DistCache.Core;

/// <summary>
/// Describes whether a peer joined or left the cluster view.
/// </summary>
public enum PeerEventKind
{
    /// <summary>
    /// A peer became visible in the topology.
    /// </summary>
    Join,

    /// <summary>
    /// A peer left or was removed from the topology.
    /// </summary>
    Leave,
}

/// <summary>
/// A topology change notification carrying the affected peer and event kind.
/// </summary>
/// <param name="Kind">Whether the peer joined or left.</param>
/// <param name="Peer">The peer that changed.</param>
public readonly record struct PeerEvent(PeerEventKind Kind, PeerInfo Peer);
