namespace DistCache.Core;

/// <summary>
/// Describes a registered cache partition: limits, TTL, backing data source, and keys warmed at startup or after updates.
/// </summary>
public interface IKeySpace
{
    /// <summary>
    /// Gets the unique name of this key space.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the maximum total size in bytes for cached entries in this key space, or <see langword="null"/> when no explicit byte limit applies.
    /// </summary>
    long? MaxBytes { get; }

    /// <summary>
    /// Gets the time-to-live for entries in this key space, or <see langword="null"/> when the engine default applies.
    /// </summary>
    TimeSpan? Ttl { get; }

    /// <summary>
    /// Gets the backing store used for read-through loading when a key is missing from the distributed cache.
    /// </summary>
    IDataSource DataSource { get; }

    /// <summary>
    /// Gets keys that were requested to be warmed for this key space.
    /// </summary>
    IReadOnlyList<string> WarmKeys { get; }

    /// <summary>
    /// Gets the interval between background sweeps that remove expired entries, or <see langword="null"/> when periodic sweeps are disabled.
    /// </summary>
    TimeSpan? SweepInterval { get; }
}
