using DistCache.Core.Abstractions;
using DistCache.Core.Concurrency;
using DistCache.Core.Configuration;
using DistCache.Core.Models;
using DistCache.Core.Registry;

namespace DistCache.Core.Engine;

/// <summary>
/// Single-node implementation of <see cref="IDistCacheEngine"/> using in-memory LRUs and read-through loading from each key space's <see cref="IKeySpace.DataSource"/>.
/// Also implements <see cref="ILocalCacheAccess"/> so the gRPC peer server can read/write the local LRU
/// directly without triggering read-through.
/// </summary>
/// <remarks>
/// Concurrent <see cref="GetAsync(string, string, CancellationToken)"/> calls for the same key space
/// and key coalesce: only one in-flight <see cref="IDataSource.FetchAsync"/> run populates the local LRU; see
/// <see cref="GetCacheStats"/>.
/// Call <see cref="DisposeAsync"/> to unregister all key spaces and dispose local caches (including background TTL sweepers).
/// </remarks>
public sealed class StandaloneEngine : IDistCacheEngine, ILocalCacheAccess, IAsyncDisposable
{
    private readonly KeySpaceRegistry registry = new();
    private readonly AsyncSingleFlight<(string KeySpace, string Key), ReadThroughCoalesceResult> readThrough = new(
        EqualityComparer<(string KeySpace, string Key)>.Default);

    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="StandaloneEngine"/> class, registers all key spaces from <paramref name="config"/>,
    /// and optionally warms keys synchronously when <see cref="KeySpaceOptions.WarmKeys"/> is non-empty.
    /// </summary>
    /// <param name="config">Engine configuration; must not be null.</param>
    public StandaloneEngine(EngineConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        foreach (KeySpaceOptions options in config.KeySpaces)
        {
            registry.Register(options);
        }

        foreach (KeySpaceOptions options in config.KeySpaces)
        {
            foreach (string warmKey in options.WarmKeys)
            {
                GetAsync(options.Name, warmKey, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<IKeySpace> KeySpaces => registry.GetDefinitions();

    /// <summary>
    /// Returns a snapshot of aggregate <see cref="CacheStats"/>. At present only
    /// <see cref="CacheStats.InFlightRequests"/> is populated; other fields are set to zero until full metrics are added.
    /// </summary>
    /// <returns>Snapshot; only <see cref="CacheStats.InFlightRequests"/> is non-zero in this build.</returns>
    public CacheStats GetCacheStats() =>
        new(0, 0, 0, 0, 0, readThrough.InFlightCount);

    /// <inheritdoc />
    public async ValueTask<CacheResult> GetAsync(string keySpaceName, string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(keySpaceName);
        ArgumentNullException.ThrowIfNull(key);

        if (!registry.TryGet(keySpaceName, out KeySpaceEntry? entry))
        {
            throw new KeyNotFoundException($"No key space named '{keySpaceName}' is registered.");
        }

        if (entry.TryGetLocal(key, out byte[]? hit))
        {
            return new CacheResult(true, hit!, keySpaceName, key);
        }

        ReadThroughCoalesceResult coalesced = await readThrough
            .ExecuteAsync(
                (keySpaceName, key),
                async ct =>
                {
                    IKeySpace definition = entry.Definition;
                    ReadOnlyMemory<byte>? fetched = await definition.DataSource.FetchAsync(key, ct).ConfigureAwait(false);
                    if (fetched is null)
                    {
                        return new ReadThroughCoalesceResult(false, null);
                    }

                    byte[] owned = fetched.Value.ToArray();
                    entry.PutLocal(key, owned);
                    return new ReadThroughCoalesceResult(true, owned);
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!coalesced.Found)
        {
            return new CacheResult(false, default, keySpaceName, key);
        }

        return new CacheResult(true, coalesced.Value!, keySpaceName, key);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, CacheResult>> GetManyAsync(
        string keySpaceName,
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(keySpaceName);
        ArgumentNullException.ThrowIfNull(keys);

        Dictionary<string, CacheResult> results = new(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            results[key] = await GetAsync(keySpaceName, key, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    /// <inheritdoc />
    public ValueTask PutAsync(
        string keySpaceName,
        string key,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(keySpaceName);
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        if (!registry.TryGet(keySpaceName, out KeySpaceEntry? entry))
        {
            throw new KeyNotFoundException($"No key space named '{keySpaceName}' is registered.");
        }

        entry.PutLocal(key, value.ToArray());
        return default;
    }

    /// <inheritdoc />
    public ValueTask PutManyAsync(
        string keySpaceName,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> entries,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(keySpaceName);
        ArgumentNullException.ThrowIfNull(entries);
        cancellationToken.ThrowIfCancellationRequested();

        if (!registry.TryGet(keySpaceName, out KeySpaceEntry? entry))
        {
            throw new KeyNotFoundException($"No key space named '{keySpaceName}' is registered.");
        }

        foreach (KeyValuePair<string, ReadOnlyMemory<byte>> pair in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entry.PutLocal(pair.Key, pair.Value.ToArray());
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(string keySpaceName, string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(keySpaceName);
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        if (!registry.TryGet(keySpaceName, out KeySpaceEntry? entry))
        {
            throw new KeyNotFoundException($"No key space named '{keySpaceName}' is registered.");
        }

        entry.RemoveLocal(key);
        return default;
    }

    /// <inheritdoc />
    public ValueTask DeleteManyAsync(
        string keySpaceName,
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(keySpaceName);
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();

        if (!registry.TryGet(keySpaceName, out KeySpaceEntry? entry))
        {
            throw new KeyNotFoundException($"No key space named '{keySpaceName}' is registered.");
        }

        foreach (string key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entry.RemoveLocal(key);
        }

        return default;
    }

    /// <inheritdoc />
    public async ValueTask DeleteKeySpaceAsync(string keySpaceName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(keySpaceName);
        cancellationToken.ThrowIfCancellationRequested();

        if (!await registry.UnregisterAsync(keySpaceName).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"No key space named '{keySpaceName}' is registered.");
        }
    }

    /// <inheritdoc />
    public ValueTask UpdateKeySpaceAsync(
        string keySpaceName,
        KeySpaceOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(keySpaceName);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(keySpaceName, options.Name, StringComparison.Ordinal))
        {
            throw new ArgumentException("Key space name must match options.Name.", nameof(keySpaceName));
        }

        registry.Update(options);
        return default;
    }

    /// <summary>
    /// Unregisters all key spaces and releases local cache resources.
    /// </summary>
    /// <returns>A task that completes when disposal finishes.</returns>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await registry.DisposeAllAsync().ConfigureAwait(false);
        disposed = true;
    }

    // ── ILocalCacheAccess (explicit) ──────────────────────────────────────

    /// <inheritdoc />
    bool ILocalCacheAccess.TryGetLocal(string keySpaceName, string key, out byte[] value)
    {
        if (!registry.TryGet(keySpaceName, out KeySpaceEntry? entry))
        {
            value = null!;
            return false;
        }

        return entry.TryGetLocal(key, out value!);
    }

    /// <inheritdoc />
    void ILocalCacheAccess.PutLocal(string keySpaceName, string key, byte[] value)
    {
        if (registry.TryGet(keySpaceName, out KeySpaceEntry? entry))
        {
            entry.PutLocal(key, value);
        }
    }

    /// <inheritdoc />
    bool ILocalCacheAccess.RemoveLocal(string keySpaceName, string key)
    {
        if (!registry.TryGet(keySpaceName, out KeySpaceEntry? entry))
        {
            return false;
        }

        entry.RemoveLocal(key);
        return true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private readonly record struct ReadThroughCoalesceResult(bool Found, byte[]? Value);
}
