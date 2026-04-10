using Grpc.Core;

namespace DistCache.Core.Exceptions;

/// <summary>
/// Raised by the gRPC peer client when a peer RPC fails.
/// Wraps the underlying <see cref="RpcException"/> and surfaces its <see cref="StatusCode"/>.
/// </summary>
public sealed class CacheException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CacheException"/> class.</summary>
    public CacheException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CacheException"/> class.</summary>
    /// <param name="message">Human-readable error description.</param>
    public CacheException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CacheException"/> class.</summary>
    /// <param name="message">Human-readable error description.</param>
    /// <param name="inner">The inner exception.</param>
    public CacheException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CacheException"/> class.</summary>
    /// <param name="message">Human-readable error description.</param>
    /// <param name="statusCode">The gRPC status code returned by the peer.</param>
    /// <param name="inner">The originating <see cref="RpcException"/>, if available.</param>
    public CacheException(string message, StatusCode statusCode, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }

    /// <summary>Gets the gRPC status code returned by the peer.</summary>
    public StatusCode StatusCode { get; }
}
