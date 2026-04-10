namespace DistCache.Core;

/// <summary>
/// Top-level engine configuration: hashing topology, key space definitions, and optional standalone host settings.
/// </summary>
/// <param name="VirtualNodeCount">
/// Number of virtual nodes per physical peer for consistent hashing, or <see langword="null"/> to use an implementation default.
/// </param>
/// <param name="KeySpaces">Key spaces to register at startup.</param>
/// <param name="Standalone">
/// When running outside a multi-peer cluster, optional standalone bind/advertise settings.
/// </param>
public sealed record EngineConfig(
    int? VirtualNodeCount,
    IReadOnlyList<KeySpaceOptions> KeySpaces,
    StandaloneConfig? Standalone = null);
