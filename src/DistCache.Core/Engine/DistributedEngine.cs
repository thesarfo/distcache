using DistCache.Core.Abstractions;
using DistCache.Core.Configuration;
using DistCache.Core.Exceptions;
using DistCache.Core.Models;
using DistCache.Core.Networking;
using DistCache.Core.Routing;
using Grpc.Core;

namespace DistCache.Core.Engine;

/// <summary>
/// Distributed <see cref="IDistCacheEngine"/> that routes each cache operation to the peer that
/// owns the key according to the consistent hash ring managed by <see cref="PeerManager"/>.
/// </summary>
/// <remarks>
/// On construction the engine registers its own endpoint in the ring so keys that hash to self
/// are served from the local <see cref="StandaloneEngine"/> without a network hop.
/// When a remote peer returns <see cref="StatusCode.Unavailable"/>, <see cref="GetAsync"/>
/// falls back to the local data source. Put and Delete propagate the error to callers.
/// <see cref="DeleteKeySpaceAsync"/> and <see cref="UpdateKeySpaceAsync"/> are local-only.
/// </remarks>
public sealed class DistributedEngine : IDistCacheEngine, IAsyncDisposable
{
    private readonly StandaloneEngine local;
    private readonly PeerManager peers;
    private readonly string selfEndpoint;
    private bool disposed;

    /// <summary>
    /// Initializes a <see cref="DistributedEngine"/> and registers the local node in the hash ring.
    /// </summary>
    /// <param name="config">
    /// Engine configuration. <see cref="EngineConfig.Standalone"/> must be set;
    /// its <see cref="StandaloneConfig.AdvertisedEndpoint"/> identifies this node in the ring.
    /// </param>
    /// <param name="peers">
    /// Peer manager holding the consistent hash ring and remote gRPC clients.
    /// Must be populated with remote peers before (or shortly after) construction.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when either argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <see cref="EngineConfig.Standalone"/> is null or
    /// <see cref="StandaloneConfig.AdvertisedEndpoint"/> is null or whitespace.
    /// </exception>
    public DistributedEngine(EngineConfig config, PeerManager peers)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(peers);

        if (config.Standalone is null)
        {
            throw new ArgumentException(
                "EngineConfig.Standalone must be set so the engine can identify itself in the ring.",
                nameof(config));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(
            config.Standalone.AdvertisedEndpoint,
            nameof(config));

        selfEndpoint = config.Standalone.AdvertisedEndpoint;
        local = new StandaloneEngine(config);
        this.peers = peers;
        peers.RegisterSelf(selfEndpoint);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<IKeySpace> KeySpaces => local.KeySpaces;

    /// <inheritdoc />
    public async ValueTask<CacheResult> GetAsync(
        string keySpaceName,
        string key,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        ICachePeerClient? client = ResolveRemoteClient(key);
        if (client is null)
        {
            return await local.GetAsync(keySpaceName, key, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            (bool found, ReadOnlyMemory<byte> value) =
                await client.GetAsync(keySpaceName, key, cancellationToken).ConfigureAwait(false);
            return new CacheResult(found, value, keySpaceName, key);
        }
        catch (CacheException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            // Owning peer is unreachable — fall back to the local data source.
            return await local.GetAsync(keySpaceName, key, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, CacheResult>> GetManyAsync(
        string keySpaceName,
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(keys);

        Dictionary<string, List<string>> byOwner = GroupKeysByOwner(keys);

        IReadOnlyDictionary<string, CacheResult>[] partials = await Task.WhenAll(
            byOwner.Select(kv => FetchGroupAsync(keySpaceName, kv.Key, kv.Value, cancellationToken)))
            .ConfigureAwait(false);

        Dictionary<string, CacheResult> merged = new(StringComparer.Ordinal);
        foreach (IReadOnlyDictionary<string, CacheResult> partial in partials)
        {
            foreach (KeyValuePair<string, CacheResult> kv in partial)
            {
                merged[kv.Key] = kv.Value;
            }
        }

        return merged;
    }

    /// <inheritdoc />
    public async ValueTask PutAsync(
        string keySpaceName,
        string key,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        ICachePeerClient? client = ResolveRemoteClient(key);
        if (client is null)
        {
            await local.PutAsync(keySpaceName, key, value, cancellationToken).ConfigureAwait(false);
            return;
        }

        await client.PutAsync(keySpaceName, key, value, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask PutManyAsync(
        string keySpaceName,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> entries,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(entries);

        Dictionary<string, List<string>> byOwner = GroupKeysByOwner(entries.Keys);

        await Task.WhenAll(byOwner.Select(async kv =>
        {
            Dictionary<string, ReadOnlyMemory<byte>> subset =
                kv.Value.ToDictionary(k => k, k => entries[k], StringComparer.Ordinal);

            ICachePeerClient? client = ResolveRemoteClientForOwner(kv.Key);
            if (client is null)
            {
                await local.PutManyAsync(keySpaceName, subset, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.BatchPutAsync(keySpaceName, subset, cancellationToken).ConfigureAwait(false);
            }
        })).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(
        string keySpaceName,
        string key,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        ICachePeerClient? client = ResolveRemoteClient(key);
        if (client is null)
        {
            await local.DeleteAsync(keySpaceName, key, cancellationToken).ConfigureAwait(false);
            return;
        }

        await client.DeleteAsync(keySpaceName, key, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DeleteManyAsync(
        string keySpaceName,
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(keys);

        Dictionary<string, List<string>> byOwner = GroupKeysByOwner(keys);

        await Task.WhenAll(byOwner.Select(async kv =>
        {
            ICachePeerClient? client = ResolveRemoteClientForOwner(kv.Key);
            if (client is null)
            {
                await local.DeleteManyAsync(keySpaceName, kv.Value, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.WhenAll(kv.Value.Select(
                    k => client.DeleteAsync(keySpaceName, k, cancellationToken)))
                    .ConfigureAwait(false);
            }
        })).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DeleteKeySpaceAsync(string keySpaceName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return local.DeleteKeySpaceAsync(keySpaceName, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask UpdateKeySpaceAsync(
        string keySpaceName,
        KeySpaceOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return local.UpdateKeySpaceAsync(keySpaceName, options, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        peers.Dispose();
        await local.DisposeAsync().ConfigureAwait(false);
        disposed = true;
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private ICachePeerClient? ResolveRemoteClient(string key)
    {
        string? owner = peers.GetOwner(key);
        return owner is null || string.Equals(owner, selfEndpoint, StringComparison.Ordinal)
            ? null
            : peers.GetClient(owner);
    }

    private ICachePeerClient? ResolveRemoteClientForOwner(string owner)
    {
        return string.Equals(owner, selfEndpoint, StringComparison.Ordinal)
            ? null
            : peers.GetClient(owner);
    }

    private Dictionary<string, List<string>> GroupKeysByOwner(IEnumerable<string> keys)
    {
        Dictionary<string, List<string>> byOwner = new(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            string owner = peers.GetOwner(key) ?? selfEndpoint;
            if (!byOwner.TryGetValue(owner, out List<string>? list))
            {
                list = [];
                byOwner[owner] = list;
            }

            list.Add(key);
        }

        return byOwner;
    }

    private async Task<IReadOnlyDictionary<string, CacheResult>> FetchGroupAsync(
        string keySpaceName,
        string owner,
        List<string> keys,
        CancellationToken ct)
    {
        ICachePeerClient? client = ResolveRemoteClientForOwner(owner);
        if (client is null)
        {
            return await local.GetManyAsync(keySpaceName, keys, ct).ConfigureAwait(false);
        }

        try
        {
            IReadOnlyDictionary<string, (bool Found, ReadOnlyMemory<byte> Value)> raw =
                await client.BatchGetAsync(keySpaceName, keys, ct).ConfigureAwait(false);

            Dictionary<string, CacheResult> result = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, (bool Found, ReadOnlyMemory<byte> Value)> kv in raw)
            {
                result[kv.Key] = new CacheResult(kv.Value.Found, kv.Value.Value, keySpaceName, kv.Key);
            }

            return result;
        }
        catch (CacheException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            return await local.GetManyAsync(keySpaceName, keys, ct).ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
