using DistCache.Core.Abstractions;
using DistCache.Core.Models;
using DistCache.Core.Networking;

namespace DistCache.Core.Routing;

/// <summary>
/// Maintains the consistent hash ring and a pool of gRPC clients for remote peers.
/// Topology updates arrive either by calling <see cref="AddPeer"/>/<see cref="RemovePeer"/>
/// directly, or by subscribing to an <see cref="IDiscoveryProvider"/> via <see cref="Subscribe"/>.
/// </summary>
public sealed class PeerManager : IDisposable
{
    private readonly ConsistentHashRing ring;
    private readonly Func<string, ICachePeerClient> clientFactory;
    private readonly Dictionary<string, ICachePeerClient> clients = new(StringComparer.Ordinal);
    private readonly object clientsLock = new();
    private IDisposable? subscription;
    private bool disposed;

    /// <summary>
    /// Initializes a new <see cref="PeerManager"/> backed by <paramref name="ring"/> and
    /// creating peer clients via <paramref name="clientFactory"/>.
    /// </summary>
    /// <param name="ring">Hash ring to update on peer join/leave events.</param>
    /// <param name="clientFactory">
    /// Factory called with a peer endpoint string to produce an <see cref="ICachePeerClient"/>.
    /// The factory is called once per unique endpoint; the returned client is reused and disposed on removal.
    /// </param>
    public PeerManager(ConsistentHashRing ring, Func<string, ICachePeerClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentNullException.ThrowIfNull(clientFactory);
        this.ring = ring;
        this.clientFactory = clientFactory;
    }

    /// <summary>
    /// Registers the local node's own endpoint in the ring without creating a client.
    /// Self-owned keys are handled locally by the engine, not via a peer client.
    /// </summary>
    /// <param name="endpoint">The local node's advertised endpoint string.</param>
    public void RegisterSelf(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ring.AddPeer(endpoint);
    }

    /// <summary>
    /// Adds a remote peer to the ring and creates a client for it.
    /// No-ops when the endpoint is already tracked.
    /// </summary>
    /// <param name="peer">The peer to add.</param>
    public void AddPeer(PeerInfo peer)
    {
        lock (clientsLock)
        {
            if (clients.ContainsKey(peer.Endpoint))
            {
                return;
            }

            ring.AddPeer(peer.Endpoint);
            clients[peer.Endpoint] = clientFactory(peer.Endpoint);
        }
    }

    /// <summary>
    /// Removes a remote peer from the ring and disposes its client.
    /// No-ops when the endpoint is not tracked.
    /// </summary>
    /// <param name="endpoint">The peer endpoint to remove.</param>
    public void RemovePeer(string endpoint)
    {
        ICachePeerClient? client;

        lock (clientsLock)
        {
            if (!clients.TryGetValue(endpoint, out client))
            {
                return;
            }

            ring.RemovePeer(endpoint);
            clients.Remove(endpoint);
        }

        client.Dispose();
    }

    /// <summary>
    /// Subscribes to topology events from <paramref name="provider"/>, calling
    /// <see cref="AddPeer"/> and <see cref="RemovePeer"/> as peers join and leave.
    /// Replaces any previously active subscription.
    /// </summary>
    /// <param name="provider">Discovery provider to subscribe to.</param>
    public void Subscribe(IDiscoveryProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        subscription?.Dispose();
        subscription = provider.PeerChanges.Subscribe(new PeerObserver(this));
    }

    /// <summary>Returns the endpoint that owns <paramref name="key"/>, or <see langword="null"/> when the ring is empty.</summary>
    public string? GetOwner(string key) => ring.GetOwner(key);

    /// <summary>
    /// Returns the <see cref="ICachePeerClient"/> for <paramref name="endpoint"/>,
    /// or <see langword="null"/> if the endpoint is not a tracked remote peer.
    /// </summary>
    public ICachePeerClient? GetClient(string endpoint)
    {
        lock (clientsLock)
        {
            return clients.TryGetValue(endpoint, out ICachePeerClient? client) ? client : null;
        }
    }

    /// <summary>Returns a point-in-time snapshot of all tracked remote peer clients.</summary>
    public IReadOnlyList<(string Endpoint, ICachePeerClient Client)> GetAllClients()
    {
        lock (clientsLock)
        {
            return clients.Select(kv => (kv.Key, kv.Value)).ToList();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        subscription?.Dispose();
        lock (clientsLock)
        {
            foreach (ICachePeerClient client in clients.Values)
            {
                client.Dispose();
            }

            clients.Clear();
        }

        disposed = true;
    }

    private sealed class PeerObserver : IObserver<PeerEvent>
    {
        private readonly PeerManager manager;

        internal PeerObserver(PeerManager manager) => this.manager = manager;

        public void OnNext(PeerEvent value)
        {
            if (value.Kind == PeerEventKind.Join)
            {
                manager.AddPeer(value.Peer);
            }
            else
            {
                manager.RemovePeer(value.Peer.Endpoint);
            }
        }

        public void OnError(Exception error) { }

        public void OnCompleted() { }
    }
}
