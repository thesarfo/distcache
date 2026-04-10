namespace DistCache.Core.Configuration;

/// <summary>
/// Settings for a single-node (non-cluster) deployment: local listen address and how this node advertises itself.
/// </summary>
/// <param name="ListenAddress">Address and port the engine binds for inter-node or client traffic.</param>
/// <param name="AdvertisedEndpoint">Endpoint other nodes or clients should use to reach this instance.</param>
/// <param name="PeerId">
/// Optional stable identity for this standalone instance; when omitted, the host may assign one at startup.
/// </param>
public sealed record StandaloneConfig(
    string ListenAddress,
    string AdvertisedEndpoint,
    string? PeerId = null);
