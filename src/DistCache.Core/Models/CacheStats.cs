namespace DistCache.Core.Models;

/// <summary>
/// Aggregate statistics for a cache region or the engine, suitable for metrics and admin surfaces.
/// </summary>
/// <param name="Hits">Number of served requests that found a value in cache.</param>
/// <param name="Misses">Number of requests that did not find a value in cache (excluding errors).</param>
/// <param name="Evictions">Number of entries removed due to capacity or TTL policy.</param>
/// <param name="EntryCount">Approximate number of entries currently held.</param>
/// <param name="ApproximateBytes">Approximate total size in bytes of cached values.</param>
/// <param name="InFlightRequests">
/// Number of read-through fetches currently in progress (request coalescing in-flight), or zero
/// when not exposed by the implementation.
/// </param>
public readonly record struct CacheStats(
    long Hits,
    long Misses,
    long Evictions,
    long EntryCount,
    long ApproximateBytes,
    long InFlightRequests);
