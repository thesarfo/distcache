namespace DistCache.Core.Routing;

/// <summary>Normalizes a peer <c>host:port</c> or full URI for <c>GrpcChannel.ForAddress</c>.</summary>
public static class GrpcPeerAddress
{
    /// <summary>
    /// When <paramref name="endpoint"/> has no <c>://</c> scheme, returns <c>https://</c> plus the endpoint; otherwise returns it unchanged.
    /// </summary>
    /// <param name="endpoint">Peer endpoint, with or without a URI scheme.</param>
    /// <returns>Absolute URI string suitable for gRPC client construction.</returns>
    public static string ToAbsoluteUriString(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return endpoint.Contains("://", StringComparison.Ordinal)
            ? endpoint
            : "https://" + endpoint;
    }
}
