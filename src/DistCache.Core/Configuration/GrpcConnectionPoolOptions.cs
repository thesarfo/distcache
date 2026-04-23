using System.Net.Http;
using Grpc.Net.Client;

namespace DistCache.Core.Configuration;

/// <summary>
/// Tuning and factory hooks for gRPC <see cref="GrpcChannel"/> instances created by
/// <see cref="Routing.PeerManager"/>'s connection pool.
/// </summary>
public sealed class GrpcConnectionPoolOptions
{
    /// <summary>Gets or sets the interval for HTTP/2 PING keepalive on pooled connections. The default is 30 seconds.</summary>
    public TimeSpan KeepAlivePingInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the timeout to wait for a keepalive PING response. The default is 10 seconds.</summary>
    public TimeSpan KeepAlivePingTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the maximum time a connection may remain idle in the pool before it is re-established. The default is 5 minutes.</summary>
    public TimeSpan PooledConnectionIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets a factory that creates a <see cref="GrpcChannel"/> instead of the default
    /// <see cref="GrpcChannel.ForAddress(string, GrpcChannelOptions)"/>, for tests or custom transport.
    /// The first argument is the full URI (after canonicalization); the second is the options from <see cref="BuildGrpcChannelOptions"/>.
    /// </summary>
    public Func<string, GrpcChannelOptions, GrpcChannel>? CreateChannel { get; set; }

    /// <summary>
    /// Builds a <see cref="GrpcChannelOptions"/> with a <see cref="SocketsHttpHandler"/> configured
    /// from the keepalive and idle settings on this instance.
    /// </summary>
    /// <returns>Options ready to pass to <see cref="GrpcChannel.ForAddress(string, GrpcChannelOptions)"/> or <see cref="CreateChannel"/>.</returns>
    public GrpcChannelOptions BuildGrpcChannelOptions()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = PooledConnectionIdleTimeout,
            KeepAlivePingDelay = KeepAlivePingInterval,
            KeepAlivePingTimeout = KeepAlivePingTimeout,
            EnableMultipleHttp2Connections = true,
        };

        return new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
        };
    }
}
