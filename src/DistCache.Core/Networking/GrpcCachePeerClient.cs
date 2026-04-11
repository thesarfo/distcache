using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using DistCache.Core.Configuration;
using DistCache.Core.Exceptions;
using DistCache.Core.Proto;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;

namespace DistCache.Core.Networking;

/// <summary>
/// <see cref="ICachePeerClient"/> implementation backed by a gRPC channel.
/// Use <see cref="Create(string)"/>, <see cref="Create(Uri, HttpClient)"/>, or
/// <see cref="Create(string, TlsOptions)"/> to construct instances.
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

    /// <summary>
    /// Creates a client that connects to <paramref name="endpoint"/> with mutual TLS.
    /// The client presents its node certificate to the server and validates the server certificate
    /// against the trusted CA in <paramref name="clientTls"/>.
    /// When <see cref="TlsOptions.AllowInsecure"/> is <see langword="true"/>, server certificate
    /// validation is skipped (development/test use only).
    /// </summary>
    /// <param name="endpoint">The peer's gRPC endpoint address (e.g. "https://peer:5001").</param>
    /// <param name="clientTls">TLS configuration providing the client certificate and trusted CA.</param>
    /// <returns>A new <see cref="GrpcCachePeerClient"/> configured for mTLS.</returns>
    public static GrpcCachePeerClient Create(string endpoint, TlsOptions clientTls)
    {
        ArgumentNullException.ThrowIfNull(clientTls);

        X509Certificate2? nodeCert = clientTls.ResolveNodeCertificate();
        X509Certificate2? trustedCa = clientTls.ResolveTrustedCa();

        // SocketsHttpHandler is the managed HTTP/2-capable handler, required for gRPC over TLS.
        // HttpClientHandler on Windows uses WinHTTP which has limited HTTP/2 + custom TLS support.
        var handler = new SocketsHttpHandler();

        var sslOptions = new SslClientAuthenticationOptions();

        if (nodeCert is not null)
        {
            sslOptions.ClientCertificates = [nodeCert];
        }

        if (clientTls.AllowInsecure)
        {
            sslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        else if (trustedCa is not null)
        {
            sslOptions.RemoteCertificateValidationCallback = (_, serverCert, _, _) =>
                serverCert is X509Certificate2 cert && ValidateServerCert(cert, trustedCa);
        }

        handler.SslOptions = sslOptions;

        return new GrpcCachePeerClient(
            GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = handler }));
    }

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

    private static bool ValidateServerCert(X509Certificate2 cert, X509Certificate2 trustedCa)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trustedCa);
        return chain.Build(cert);
    }
}
