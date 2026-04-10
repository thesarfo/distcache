namespace DistCache.Core.Networking;

/// <summary>
/// Outbound client for a single remote cache peer.
/// Implementations must be disposed to release the underlying channel.
/// </summary>
public interface ICachePeerClient : IDisposable
{
    /// <summary>Fetches a single key from the remote peer's local LRU (no read-through on the peer).</summary>
    /// <param name="keySpace">The key space to query.</param>
    /// <param name="key">Key to look up.</param>
    /// <param name="ct">Token to cancel the operation.</param>
    /// <returns>A tuple indicating whether the key was found and its value bytes.</returns>
    Task<(bool Found, ReadOnlyMemory<byte> Value)> GetAsync(
        string keySpace,
        string key,
        CancellationToken ct = default);

    /// <summary>Stores a single key/value on the remote peer.</summary>
    /// <param name="keySpace">The key space to write to.</param>
    /// <param name="key">Key to store.</param>
    /// <param name="value">Value bytes to store.</param>
    /// <param name="ct">Token to cancel the operation.</param>
    /// <returns>A task that completes when the peer acknowledges the write.</returns>
    Task PutAsync(
        string keySpace,
        string key,
        ReadOnlyMemory<byte> value,
        CancellationToken ct = default);

    /// <summary>Deletes a single key on the remote peer.</summary>
    /// <param name="keySpace">The key space to modify.</param>
    /// <param name="key">Key to remove.</param>
    /// <param name="ct">Token to cancel the operation.</param>
    /// <returns>A task that completes when the peer acknowledges the delete.</returns>
    Task DeleteAsync(
        string keySpace,
        string key,
        CancellationToken ct = default);

    /// <summary>Fetches multiple keys from the remote peer in a single RPC.</summary>
    /// <param name="keySpace">The key space to query.</param>
    /// <param name="keys">Keys to look up.</param>
    /// <param name="ct">Token to cancel the operation.</param>
    /// <returns>Per-key found/value results keyed by the requested key strings.</returns>
    Task<IReadOnlyDictionary<string, (bool Found, ReadOnlyMemory<byte> Value)>> BatchGetAsync(
        string keySpace,
        IEnumerable<string> keys,
        CancellationToken ct = default);

    /// <summary>Stores multiple key/value pairs on the remote peer in a single RPC.</summary>
    /// <param name="keySpace">The key space to write to.</param>
    /// <param name="entries">Key/value pairs to store.</param>
    /// <param name="ct">Token to cancel the operation.</param>
    /// <returns>A task that completes when the peer acknowledges all writes.</returns>
    Task BatchPutAsync(
        string keySpace,
        IEnumerable<KeyValuePair<string, ReadOnlyMemory<byte>>> entries,
        CancellationToken ct = default);
}
