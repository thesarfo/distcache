using System.Linq;
using DistCache.Core.Abstractions;
using DistCache.Core.Models;

namespace DistCache.Discovery;

/// <summary>
/// A fixed, in-memory <see cref="IDiscoveryProvider"/> for tests and demos. Optional initial peers are
/// announced as <see cref="PeerEventKind.Join"/> from <see cref="StartAsync"/>. Use <see cref="SetPeers"/>
/// to update membership and emit join/leave events.
/// </summary>
public sealed class StaticDiscoveryProvider : IDiscoveryProvider
{
    private readonly object sync = new();
    private readonly List<IObserver<PeerEvent>> observers = new();
    private readonly IReadOnlyList<PeerInfo>? seedPeers;
    private IReadOnlyList<PeerInfo> peers = Array.Empty<PeerInfo>();

    /// <summary>Initializes a new instance with optional <paramref name="initialPeers"/> announced from <see cref="StartAsync"/>.</summary>
    /// <param name="initialPeers">Peers to register when <see cref="StartAsync"/> is called, or <see langword="null"/> for an empty set.</param>
    public StaticDiscoveryProvider(IReadOnlyList<PeerInfo>? initialPeers = null) => seedPeers = initialPeers;

    /// <inheritdoc />
    public IObservable<PeerEvent> PeerChanges => new PeerChangesObservable(this);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (seedPeers is not { Count: > 0 })
        {
            return Task.CompletedTask;
        }

        List<PeerEvent> toEmit;
        lock (sync)
        {
            peers = seedPeers.ToList();
            toEmit = peers.Select(p => new PeerEvent(PeerEventKind.Join, p)).ToList();
        }

        foreach (PeerEvent e in toEmit)
        {
            Emit(e);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PeerInfo>> GetPeersAsync(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            return Task.FromResult<IReadOnlyList<PeerInfo>>(peers.ToList());
        }
    }

    /// <summary>
    /// Replaces the known peer set, emitting <see cref="PeerEventKind.Leave"/> for removed endpoints
    /// and <see cref="PeerEventKind.Join"/> for new ones. Compares by <see cref="PeerInfo.Endpoint"/> (ordinal).
    /// </summary>
    /// <param name="next">The new peer set.</param>
    public void SetPeers(IReadOnlyList<PeerInfo> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        List<PeerEvent> toEmit;
        lock (sync)
        {
            HashSet<string> oldEndpoints = new(StringComparer.Ordinal);
            foreach (PeerInfo p in peers)
            {
                oldEndpoints.Add(p.Endpoint);
            }

            HashSet<string> newEndpoints = new(StringComparer.Ordinal);
            foreach (PeerInfo p in next)
            {
                newEndpoints.Add(p.Endpoint);
            }

            toEmit = new List<PeerEvent>();
            foreach (PeerInfo p in peers)
            {
                if (!newEndpoints.Contains(p.Endpoint))
                {
                    toEmit.Add(new PeerEvent(PeerEventKind.Leave, p));
                }
            }

            foreach (PeerInfo p in next)
            {
                if (!oldEndpoints.Contains(p.Endpoint))
                {
                    toEmit.Add(new PeerEvent(PeerEventKind.Join, p));
                }
            }

            peers = next.ToList();
        }

        foreach (PeerEvent e in toEmit)
        {
            Emit(e);
        }
    }

    private void Emit(PeerEvent e)
    {
        IObserver<PeerEvent>[] snapshot;
        lock (sync)
        {
            snapshot = observers.ToArray();
        }

        foreach (IObserver<PeerEvent> o in snapshot)
        {
            o.OnNext(e);
        }
    }

    private void AddObserver(IObserver<PeerEvent> o)
    {
        lock (sync)
        {
            observers.Add(o);
        }
    }

    private void RemoveObserver(IObserver<PeerEvent> o)
    {
        lock (sync)
        {
            observers.Remove(o);
        }
    }

    private sealed class PeerChangesObservable : IObservable<PeerEvent>
    {
        private readonly StaticDiscoveryProvider parent;

        public PeerChangesObservable(StaticDiscoveryProvider parent) => this.parent = parent;

        public IDisposable Subscribe(IObserver<PeerEvent> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            parent.AddObserver(observer);
            return new Unsubscribe(parent, observer);
        }
    }

    private sealed class Unsubscribe : IDisposable
    {
        private readonly StaticDiscoveryProvider parent;
        private readonly IObserver<PeerEvent> observer;

        public Unsubscribe(StaticDiscoveryProvider parent, IObserver<PeerEvent> observer)
        {
            this.parent = parent;
            this.observer = observer;
        }

        public void Dispose() => parent.RemoveObserver(observer);
    }
}
