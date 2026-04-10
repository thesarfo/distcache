using System.Diagnostics.CodeAnalysis;

namespace DistCache.Core.Abstractions;

/// <summary>
/// Exposes raw local-LRU operations without read-through, used by the gRPC peer server
/// to answer peer requests from its own in-memory state only.
/// </summary>
public interface ILocalCacheAccess
{
    /// <summary>
    /// Reads a value from the local LRU for <paramref name="keySpaceName"/> without
    /// triggering a read-through fetch from the backing data source.
    /// </summary>
    /// <param name="keySpaceName">Name of the key space to query.</param>
    /// <param name="key">Key to look up.</param>
    /// <param name="value">The stored bytes when the method returns <see langword="true"/>; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the key is present and not expired; otherwise <see langword="false"/>.</returns>
    bool TryGetLocal(string keySpaceName, string key, [MaybeNullWhen(false)] out byte[] value);

    /// <summary>Writes a value directly into the local LRU. No-ops if the key space is not registered.</summary>
    /// <param name="keySpaceName">Name of the key space to write to.</param>
    /// <param name="key">Key to store.</param>
    /// <param name="value">Value bytes to store.</param>
    void PutLocal(string keySpaceName, string key, byte[] value);

    /// <summary>Removes a key from the local LRU.</summary>
    /// <param name="keySpaceName">Name of the key space to modify.</param>
    /// <param name="key">Key to remove.</param>
    /// <returns><see langword="true"/> if the key space was found; otherwise <see langword="false"/>.</returns>
    bool RemoveLocal(string keySpaceName, string key);
}
