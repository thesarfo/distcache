using DistCache.Core.Configuration;
using DistCache.Core.Models;

namespace DistCache.Core.Abstractions;

/// <summary>
/// Distributed cache engine: read-through gets, writes, deletes, and live key space management.
/// </summary>
public interface IDistCacheEngine
{
    /// <summary>
    /// Gets a snapshot of key spaces currently known to the engine.
    /// </summary>
    IReadOnlyCollection<IKeySpace> KeySpaces { get; }

    /// <summary>
    /// Reads a single key from the named key space (read-through on miss when configured).
    /// </summary>
    /// <param name="keySpaceName">Key space to query.</param>
    /// <param name="key">Key to read.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The cache result for this key.</returns>
    ValueTask<CacheResult> GetAsync(string keySpaceName, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads multiple keys from the named key space in one logical operation.
    /// </summary>
    /// <param name="keySpaceName">Key space to query.</param>
    /// <param name="keys">Keys to read.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Per-key results; keys with no value have <see cref="CacheResult.Found"/> set to <see langword="false"/>.</returns>
    ValueTask<IReadOnlyDictionary<string, CacheResult>> GetManyAsync(
        string keySpaceName,
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a value under a key in the named key space.
    /// </summary>
    /// <param name="keySpaceName">Key space to write to.</param>
    /// <param name="key">Key to write.</param>
    /// <param name="value">Value bytes to store.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when the write is acknowledged.</returns>
    ValueTask PutAsync(
        string keySpaceName,
        string key,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores multiple entries in the named key space.
    /// </summary>
    /// <param name="keySpaceName">Key space to write to.</param>
    /// <param name="entries">Keys and values to store.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when the writes are acknowledged.</returns>
    ValueTask PutManyAsync(
        string keySpaceName,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> entries,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a key from the named key space across the cluster as defined by the implementation.
    /// </summary>
    /// <param name="keySpaceName">Key space to modify.</param>
    /// <param name="key">Key to remove.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when the delete is acknowledged.</returns>
    ValueTask DeleteAsync(string keySpaceName, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes multiple keys from the named key space.
    /// </summary>
    /// <param name="keySpaceName">Key space to modify.</param>
    /// <param name="keys">Keys to remove.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when the deletes are acknowledged.</returns>
    ValueTask DeleteManyAsync(
        string keySpaceName,
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops an entire key space and its cached data according to implementation policy.
    /// </summary>
    /// <param name="keySpaceName">Name of the key space to remove.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when the key space is removed.</returns>
    ValueTask DeleteKeySpaceAsync(string keySpaceName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates configuration for an existing key space (e.g. TTL, limits, data source, warm keys).
    /// </summary>
    /// <param name="keySpaceName">Name of the key space to update.</param>
    /// <param name="options">New options; <see cref="KeySpaceOptions.Name"/> should match <paramref name="keySpaceName"/>.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when the update is applied.</returns>
    ValueTask UpdateKeySpaceAsync(
        string keySpaceName,
        KeySpaceOptions options,
        CancellationToken cancellationToken = default);
}
