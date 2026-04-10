namespace DistCache.Core.Models;

/// <summary>
/// Identifies a peer in the cluster for routing and topology updates.
/// </summary>
/// <param name="Id">Stable identifier for this peer within the cluster.</param>
/// <param name="Endpoint">Host name or address (and optional port) used to reach the peer.</param>
public readonly record struct PeerInfo(string Id, string Endpoint);
