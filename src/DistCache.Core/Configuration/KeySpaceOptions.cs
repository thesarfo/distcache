namespace DistCache.Core;

/// <summary>
/// Configuration for registering or updating a key space: TTL, size limits, backing data source, and optional warm keys.
/// </summary>
/// <param name="Name">Unique name of the key space.</param>
/// <param name="MaxBytes">
/// Maximum total size in bytes for cached entries in this key space, or <see langword="null"/> for no explicit byte limit.
/// </param>
/// <param name="Ttl">Time-to-live for entries, or <see langword="null"/> to use engine defaults.</param>
/// <param name="DataSource">Backing store used for read-through loading on miss.</param>
/// <param name="WarmKeys">Keys to prefetch into the cache when the key space is created or updated.</param>
public sealed record KeySpaceOptions(
    string Name,
    long? MaxBytes,
    TimeSpan? Ttl,
    IDataSource DataSource,
    IReadOnlyList<string> WarmKeys);
