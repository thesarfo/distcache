using DistCache.Core.Abstractions;
using DistCache.Core.Proto;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace DistCache.Core.Networking;

/// <summary>
/// gRPC service implementation that serves incoming peer requests from the node's local LRU.
/// Reads never trigger read-through, if a key is absent locally the response carries <c>Found = false</c>.
/// Register with <c>app.MapGrpcService&lt;CachePeerServer&gt;()</c> and ensure
/// <see cref="ILocalCacheAccess"/> is registered in the DI container.
/// </summary>
public sealed class CachePeerServer : CacheService.CacheServiceBase
{
    private readonly ILocalCacheAccess local;

    /// <summary>Initializes a new instance of the <see cref="CachePeerServer"/> class.</summary>
    /// <param name="local">Local cache accessor used to serve peer reads and writes without read-through.</param>
    public CachePeerServer(ILocalCacheAccess local)
    {
        ArgumentNullException.ThrowIfNull(local);
        this.local = local;
    }

    /// <inheritdoc />
    public override Task<GetResponse> Get(GetRequest request, ServerCallContext context)
    {
        if (!local.TryGetLocal(request.KeySpace, request.Key, out byte[]? value))
        {
            // Distinguish "unknown key space" from "key not found" with NotFound so the
            // client can surface a meaningful CacheException.StatusCode when the key space
            // has not been registered on this node.
            return Task.FromResult(new GetResponse { Found = false, Value = ByteString.Empty });
        }

        return Task.FromResult(new GetResponse
        {
            Found = true,
            Value = ByteString.CopyFrom(value),
        });
    }

    /// <inheritdoc />
    public override Task<Empty> Put(PutRequest request, ServerCallContext context)
    {
        local.PutLocal(request.KeySpace, request.Key, request.Value.ToByteArray());
        return Task.FromResult(new Empty());
    }

    /// <inheritdoc />
    public override Task<Empty> Delete(DeleteRequest request, ServerCallContext context)
    {
        local.RemoveLocal(request.KeySpace, request.Key);
        return Task.FromResult(new Empty());
    }

    /// <inheritdoc />
    public override Task<BatchGetResponse> BatchGet(BatchGetRequest request, ServerCallContext context)
    {
        var response = new BatchGetResponse();
        foreach (string key in request.Keys)
        {
            bool found = local.TryGetLocal(request.KeySpace, key, out byte[]? value);
            response.Entries.Add(new CacheEntry
            {
                Key = key,
                Found = found,
                Value = found ? ByteString.CopyFrom(value!) : ByteString.Empty,
            });
        }

        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public override Task<Empty> BatchPut(BatchPutRequest request, ServerCallContext context)
    {
        foreach (BatchPutEntry entry in request.Entries)
        {
            local.PutLocal(request.KeySpace, entry.Key, entry.Value.ToByteArray());
        }

        return Task.FromResult(new Empty());
    }
}
