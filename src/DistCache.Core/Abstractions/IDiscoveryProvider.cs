using DistCache.Core.Models;

namespace DistCache.Core.Abstractions;

/// <summary>
/// Discovers cluster peers and exposes a stream of join/leave events for topology-aware routing.
/// </summary>
public interface IDiscoveryProvider
{
    /// <summary>
    /// Gets a cold observable of peer join and leave events. Implementations push updates after <see cref="StartAsync"/>.
    /// </summary>
    IObservable<PeerEvent> PeerChanges { get; }

    /// <summary>
    /// Starts background discovery and subscription lifecycle.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel startup.</param>
    /// <returns>A task that completes when the provider is ready to report peers, or faults on fatal error.</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current peer list snapshot from the provider's last known view.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Peers currently considered active.</returns>
    Task<IReadOnlyList<PeerInfo>> GetPeersAsync(CancellationToken cancellationToken = default);
}
