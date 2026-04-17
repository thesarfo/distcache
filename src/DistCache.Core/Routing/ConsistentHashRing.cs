using System.Text;

namespace DistCache.Core.Routing;

/// <summary>
/// Describes a transfer of hash-ring segment ownership when a peer joins or leaves the cluster.
/// The segment is the arc of hash space whose upper boundary is <see cref="VnodePosition"/>;
/// every cache key whose FNV-1a hash falls in that arc has changed hands.
/// </summary>
/// <param name="PreviousOwner">The peer endpoint that owned this segment before the topology change.</param>
/// <param name="NewOwner">The peer endpoint that owns this segment after the topology change.</param>
/// <param name="VnodePosition">
/// The 32-bit ring position at which this segment ends (inclusive).
/// The segment begins immediately after the preceding virtual-node position.
/// </param>
public sealed record OwnershipDiff(string PreviousOwner, string NewOwner, uint VnodePosition);

/// <summary>
/// Thread-safe consistent hash ring with configurable virtual-node replication.
/// </summary>
/// <remarks>
/// Each physical peer occupies <see cref="ReplicaCount"/> virtual nodes distributed around
/// a 32-bit ring using FNV-1a hashing of <c>"endpoint:N"</c> labels.
/// <see cref="GetOwner"/> performs a clockwise lookup (first virtual node ≥ hash, wrapping
/// to the lowest node when the hash exceeds every position).
/// <see cref="AddPeer"/> and <see cref="RemovePeer"/> return per-vnode
/// <see cref="OwnershipDiff"/> records so callers can migrate affected key ranges.
/// </remarks>
public sealed class ConsistentHashRing
{
    private readonly int _replicaCount;

    private readonly SortedDictionary<uint, string> _ring = new();

    private readonly Dictionary<string, uint[]> _peerPositions = new(StringComparer.Ordinal);

    private readonly object _lock = new();

    /// <summary>Gets the number of virtual nodes registered per physical peer.</summary>
    public int ReplicaCount => _replicaCount;

    /// <summary>Gets the total number of virtual nodes currently in the ring.</summary>
    public int VirtualNodeCount
    {
        get
        {
            lock (_lock)
            {
                return _ring.Count;
            }
        }
    }

    /// <summary>Gets a snapshot of the physical peer endpoints currently in the ring.</summary>
    public IReadOnlyList<string> Peers
    {
        get
        {
            lock (_lock)
            {
                return [.. _peerPositions.Keys];
            }
        }
    }

    /// <summary>
    /// Initializes a new <see cref="ConsistentHashRing"/> with the specified replica count.
    /// </summary>
    /// <param name="replicaCount">
    /// Number of virtual nodes per physical peer. Higher values improve key distribution at the
    /// cost of slightly more memory. Defaults to 150.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="replicaCount"/> is zero or negative.
    /// </exception>
    public ConsistentHashRing(int replicaCount = 150)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(replicaCount);
        _replicaCount = replicaCount;
    }

    /// <summary>
    /// Returns the peer that owns <paramref name="key"/> according to the current ring state,
    /// or <see langword="null"/> if the ring is empty.
    /// </summary>
    /// <param name="key">The cache key to look up.</param>
    /// <returns>
    /// The owning peer's endpoint string, or <see langword="null"/> when no peers are registered.
    /// </returns>
    public string? GetOwner(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_lock)
        {
            return GetOwnerByHashUnlocked(FnvHash(key));
        }
    }

    /// <summary>
    /// Registers <paramref name="endpoint"/> in the ring and returns the set of virtual-node
    /// positions whose ownership transferred from an existing peer to the new one.
    /// </summary>
    /// <param name="endpoint">
    /// The peer's endpoint address, used as both the ring identity and the vnode label seed.
    /// </param>
    /// <returns>
    /// One <see cref="OwnershipDiff"/> per virtual node where the new peer displaced an existing
    /// peer. Returns an empty list when the ring was empty before this call (no previous owner)
    /// or when <paramref name="endpoint"/> was already registered.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="endpoint"/> is null, empty, or whitespace.
    /// </exception>
    public IReadOnlyList<OwnershipDiff> AddPeer(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var diffs = new List<OwnershipDiff>();

        lock (_lock)
        {
            if (_peerPositions.ContainsKey(endpoint))
            {
                return [];
            }

            uint[] positions = new uint[_replicaCount];

            for (int i = 0; i < _replicaCount; i++)
            {
                // Put the replica index first so FNV-1a sees diverse bytes from byte 0,
                // giving independent hash values across both index and peer identity.
                uint pos = FnvHash($"{i}:{endpoint}");
                positions[i] = pos;

                // Capture who currently owns this ring position before the new peer claims it.
                string? previousOwner = GetOwnerByHashUnlocked(pos);

                _ring[pos] = endpoint;

                // Only emit a diff when there is a real ownership handover.
                if (previousOwner is not null && previousOwner != endpoint)
                {
                    diffs.Add(new OwnershipDiff(previousOwner, endpoint, pos));
                }
            }

            _peerPositions[endpoint] = positions;
        }

        return diffs;
    }

    /// <summary>
    /// Removes <paramref name="endpoint"/> from the ring and returns the set of virtual-node
    /// positions whose ownership transferred from the departing peer to its successors.
    /// </summary>
    /// <param name="endpoint">The peer's endpoint address.</param>
    /// <returns>
    /// One <see cref="OwnershipDiff"/> per virtual node reassigned to a surviving peer.
    /// Returns an empty list when <paramref name="endpoint"/> was not registered or when no
    /// peers remain after removal (no successor to receive ownership).
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="endpoint"/> is null, empty, or whitespace.
    /// </exception>
    public IReadOnlyList<OwnershipDiff> RemovePeer(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var diffs = new List<OwnershipDiff>();

        lock (_lock)
        {
            if (!_peerPositions.TryGetValue(endpoint, out uint[]? positions))
            {
                return [];
            }

            // Remove all virtual nodes first so successor lookups below reflect the
            // post-removal ring state and point to surviving peers only.
            foreach (uint pos in positions)
            {
                _ring.Remove(pos);
            }

            _peerPositions.Remove(endpoint);

            // Each removed position now belongs to whichever surviving peer is next on the ring.
            foreach (uint pos in positions)
            {
                string? newOwner = GetOwnerByHashUnlocked(pos);
                if (newOwner is not null)
                {
                    diffs.Add(new OwnershipDiff(endpoint, newOwner, pos));
                }
            }
        }

        return diffs;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// FNV-1a 32-bit hash of the UTF-8 encoding of <paramref name="input"/>.
    /// Stack-allocates the byte buffer for inputs up to 512 bytes; heap-allocates beyond that.
    /// </summary>
    private static uint FnvHash(string input)
    {
        const uint Prime = 16777619u;
        const uint OffsetBasis = 2166136261u;

        int byteCount = Encoding.UTF8.GetByteCount(input);
        Span<byte> buffer = byteCount <= 512
            ? stackalloc byte[byteCount]
            : new byte[byteCount];

        Encoding.UTF8.GetBytes(input, buffer);

        uint hash = OffsetBasis;
        foreach (byte b in buffer)
        {
            hash ^= b;
            hash *= Prime;
        }

        return hash;
    }

    // Must be called with _lock held.
    private string? GetOwnerByHashUnlocked(uint hash)
    {
        if (_ring.Count == 0)
        {
            return null;
        }

        // Clockwise successor: first virtual node at or past the hash value.
        foreach (KeyValuePair<uint, string> entry in _ring)
        {
            if (entry.Key >= hash)
            {
                return entry.Value;
            }
        }

        // Hash exceeds every node position — wrap around to the start of the ring.
        return _ring.Values.First();
    }
}
