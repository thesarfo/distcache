namespace DistCache.Core;

/// <summary>
/// Outcome of a single-key cache read.
/// </summary>
/// <param name="Found">
/// <see langword="true"/> if a value was present (hit or successful read-through); otherwise <see langword="false"/>.
/// </param>
/// <param name="Value">Payload when <paramref name="Found"/> is <see langword="true"/>; otherwise empty.</param>
/// <param name="KeySpaceName">The key space that was queried, if known.</param>
/// <param name="Key">The key that was queried, if known.</param>
public readonly record struct CacheResult(
    bool Found,
    ReadOnlyMemory<byte> Value,
    string? KeySpaceName = null,
    string? Key = null);
