using DistCache.Core.Exceptions;
using DistCache.Core.Proto;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;

namespace DistCache.Core.Networking;

/// <summary>
/// <see cref="ICachePeerClient"/> implementation backed by a gRPC channel.
/// Use <see cref="Create(string)"/> or <see cref="Create(Uri, HttpClient)"/> to construct instances.
/// </summary>
public sealed class GrpcCachePeerClient : ICachePeerClient
{
    private readonly GrpcChannel channel;
    private readonly CacheService.CacheServiceClient grpc;

    private GrpcCachePeerClient(GrpcChannel channel)
    {
        this.channel = channel;
        grpc = new CacheService.CacheServiceClient(channel);
    }

    /// <summary>Creates a client that connects to <paramref name="endpoint"/> (e.g. "https://peer:5001").</summary>
    /// <param name="endpoint">The peer's gRPC endpoint address.</param>
    /// <returns>A new <see cref="GrpcCachePeerClient"/> connected to <paramref name="endpoint"/>.</returns>
    public static GrpcCachePeerClient Create(string endpoint)
        => new(GrpcChannel.ForAddress(endpoint));

    /// <summary>
    /// Creates a client using an existing <paramref name="httpClient"/>, intended for in-process tests
    /// (e.g. <c>WebApplication.GetTestClient()</c> from <c>Microsoft.AspNetCore.TestHost</c>).
    /// </summary>
    /// <param name="address">The base address for the gRPC channel.</param>
    /// <param name="httpClient">The <see cref="HttpClient"/> to use for all requests (not disposed by this instance).</param>
    /// <returns>A new <see cref="GrpcCachePeerClient"/> backed by the supplied client.</returns>
    public static GrpcCachePeerClient Create(Uri address, HttpClient httpClient)
        => new(GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpClient = httpClient }));

    /// <inheritdoc />
    public async Task<(bool Found, ReadOnlyMemory<byte> Value)> GetAsync(
        string keySpace,
        string key,
        CancellationToken ct = default)
    {
        try
        {
            GetResponse response = await grpc.GetAsync(
                new GetRequest { KeySpace = keySpace, Key = key },
                cancellationToken: ct);
            return (response.Found, response.Value.ToByteArray());
        }
        catch (RpcException ex)
        {
            throw new CacheException(ex.Status.Detail, ex.StatusCode, ex);
        }
    }

    /// <inheritdoc />
    public async Task PutAsync(
        string keySpace,
        string key,
        ReadOnlyMemory<byte> value,
        CancellationToken ct = default)
    {
        try
        {
            await grpc.PutAsync(
                new PutRequest
                {
                    KeySpace = keySpace,
                    Key = key,
                    Value = ByteString.CopyFrom(value.Span),
                },
                cancellationToken: ct);
        }
        catch (RpcException ex)
        {
            throw new CacheException(ex.Status.Detail, ex.StatusCode, ex);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(
        string keySpace,
        string key,
        CancellationToken ct = default)
    {
        try
        {
            await grpc.DeleteAsync(
                new DeleteRequest { KeySpace = keySpace, Key = key },
                cancellationToken: ct);
        }
        catch (RpcException ex)
        {
            throw new CacheException(ex.Status.Detail, ex.StatusCode, ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, (bool Found, ReadOnlyMemory<byte> Value)>> BatchGetAsync(
        string keySpace,
        IEnumerable<string> keys,
        CancellationToken ct = default)
    {
        try
        {
            var request = new BatchGetRequest { KeySpace = keySpace };
            request.Keys.AddRange(keys);
            BatchGetResponse response = await grpc.BatchGetAsync(request, cancellationToken: ct);

            Dictionary<string, (bool, ReadOnlyMemory<byte>)> result = new(StringComparer.Ordinal);
            foreach (CacheEntry entry in response.Entries)
            {
                result[entry.Key] = (entry.Found, entry.Value.ToByteArray());
            }

            return result;
        }
        catch (RpcException ex)
        {
            throw new CacheException(ex.Status.Detail, ex.StatusCode, ex);
        }
    }

    /// <inheritdoc />
    public async Task BatchPutAsync(
        string keySpace,
        IEnumerable<KeyValuePair<string, ReadOnlyMemory<byte>>> entries,
        CancellationToken ct = default)
    {
        try
        {
            var request = new BatchPutRequest { KeySpace = keySpace };
            foreach (KeyValuePair<string, ReadOnlyMemory<byte>> pair in entries)
            {
                request.Entries.Add(new BatchPutEntry
                {
                    Key = pair.Key,
                    Value = ByteString.CopyFrom(pair.Value.Span),
                });
            }

            await grpc.BatchPutAsync(request, cancellationToken: ct);
        }
        catch (RpcException ex)
        {
            throw new CacheException(ex.Status.Detail, ex.StatusCode, ex);
        }
    }

    /// <inheritdoc />
    public void Dispose() => channel.Dispose();
}
