namespace DistCache.Core.Abstractions;

/// <summary>
/// Fetches cache values from a backing store for read-through loading when a key is missing from the distributed cache.
/// </summary>
public interface IDataSource
{
    /// <summary>
    /// Loads the raw value for a key from the backing store.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    /// The value bytes when the key exists; <see langword="null"/> when the key is not present at the source
    /// (cache miss at origin).
    /// </returns>
    ValueTask<ReadOnlyMemory<byte>?> FetchAsync(string key, CancellationToken cancellationToken = default);
}
