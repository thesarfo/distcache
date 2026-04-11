using System.Security.Cryptography.X509Certificates;
using System.Text;
using DistCache.Admin.Extensions;
using DistCache.Core.Abstractions;
using DistCache.Core.Configuration;
using DistCache.Core.Engine;
using DistCache.Core.Exceptions;
using DistCache.Core.Networking;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace DistCache.Tests.Networking;

/// <summary>
/// Integration tests verifying mutual TLS between cache nodes.
/// Each test spins up a real Kestrel server on port 0 (OS-assigned) so the full TLS
/// handshake is exercised, <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> bypasses TLS
/// and cannot be used here.
/// </summary>
public sealed class CachePeerMtlsTests
{
    private const string KeySpace = "mtls-ks";
    private static readonly KeySpaceOptions Options = new(
        Name: KeySpace,
        MaxBytes: 1024 * 1024,
        Ttl: null,
        DataSource: NullDataSource.Instance,
        WarmKeys: []);

    // One CA and one node cert shared across all tests in this instance.
    private readonly X509Certificate2 _trustedCa;
    private readonly X509Certificate2 _nodeCert;

    public CachePeerMtlsTests()
    {
        _trustedCa = TestCertificateAuthority.CreateCa();
        _nodeCert = TestCertificateAuthority.IssueNode(_trustedCa, "distcache-test-node");
    }


    [Fact]
    public async Task MtlsHandshake_with_valid_client_cert_succeeds()
    {
        TlsOptions tls = new()
        {
            NodeCertificate = _nodeCert,
            TrustedCaCertificate = _trustedCa,
        };

        await using StandaloneEngine engine = new(new EngineConfig(null, [Options]));
        (WebApplication node, string address) = await StartMtlsNode(tls, engine);

        using GrpcCachePeerClient client = GrpcCachePeerClient.Create(address, tls);

        byte[] payload = Encoding.UTF8.GetBytes("hello-mtls");
        await client.PutAsync(KeySpace, "k1", payload);
        (bool found, ReadOnlyMemory<byte> value) = await client.GetAsync(KeySpace, "k1");

        found.Should().BeTrue();
        value.ToArray().Should().Equal(payload);

        await node.StopAsync();
    }



    [Fact]
    public async Task Missing_client_cert_is_rejected()
    {
        TlsOptions serverTls = new()
        {
            NodeCertificate = _nodeCert,
            TrustedCaCertificate = _trustedCa,
        };

        await using StandaloneEngine engine = new(new EngineConfig(null, [Options]));
        (WebApplication node, string address) = await StartMtlsNode(serverTls, engine);

        // Client presents no certificate, only trusts the server CA.
        TlsOptions clientTls = new() { TrustedCaCertificate = _trustedCa };
        using GrpcCachePeerClient client = GrpcCachePeerClient.Create(address, clientTls);

        Func<Task> act = () => client.GetAsync(KeySpace, "k");

        await act.Should().ThrowAsync<CacheException>();

        await node.StopAsync();
    }


    [Fact]
    public async Task Client_cert_from_wrong_CA_is_rejected()
    {
        TlsOptions serverTls = new()
        {
            NodeCertificate = _nodeCert,
            TrustedCaCertificate = _trustedCa,
        };

        await using StandaloneEngine engine = new(new EngineConfig(null, [Options]));
        (WebApplication node, string address) = await StartMtlsNode(serverTls, engine);

        // Cert issued by a completely different CA, server's trust store won't accept it.
        X509Certificate2 rogueCert = TestCertificateAuthority.IssueNode(
            TestCertificateAuthority.CreateCa(), "rogue-node");

        TlsOptions clientTls = new()
        {
            NodeCertificate = rogueCert,
            TrustedCaCertificate = _trustedCa,
        };

        using GrpcCachePeerClient client = GrpcCachePeerClient.Create(address, clientTls);

        Func<Task> act = () => client.GetAsync(KeySpace, "k");

        await act.Should().ThrowAsync<CacheException>();

        await node.StopAsync();
    }



    [Fact]
    public async Task AllowInsecure_flag_bypasses_cert_validation()
    {
        // Server presents a cert but does not require one from the client.
        TlsOptions serverTls = new()
        {
            NodeCertificate = _nodeCert,
            AllowInsecure = true,
        };

        await using StandaloneEngine engine = new(new EngineConfig(null, [Options]));
        (WebApplication node, string address) = await StartMtlsNode(serverTls, engine);

        // Client skips server cert validation entirely.
        TlsOptions clientTls = new() { AllowInsecure = true };
        using GrpcCachePeerClient client = GrpcCachePeerClient.Create(address, clientTls);

        byte[] payload = Encoding.UTF8.GetBytes("insecure-ok");
        await client.PutAsync(KeySpace, "k-dev", payload);
        (bool found, ReadOnlyMemory<byte> value) = await client.GetAsync(KeySpace, "k-dev");

        found.Should().BeTrue();
        value.ToArray().Should().Equal(payload);

        await node.StopAsync();
    }



    private static async Task<(WebApplication App, string Address)> StartMtlsNode(
        TlsOptions serverTls,
        StandaloneEngine engine)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        // Bind on a Kestrel-managed HTTPS/HTTP2 endpoint on a random OS-assigned port.
        builder.WebHost.ConfigureKestrel(k =>
            k.Listen(System.Net.IPAddress.Loopback, 0, lo =>
            {
                lo.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
                lo.UseCachePeerMtls(serverTls);
            }));

        builder.Services.AddGrpc();
        builder.Services.AddSingleton<ILocalCacheAccess>(engine);

        WebApplication app = builder.Build();
        app.MapGrpcService<CachePeerServer>();
        await app.StartAsync();

        
        IServer server = app.Services.GetRequiredService<IServer>();
        IServerAddressesFeature? addressFeature = server.Features.Get<IServerAddressesFeature>();
        string address = addressFeature?.Addresses.First()
            ?? throw new InvalidOperationException("Could not determine server address after start.");

        return (app, address);
    }


    private sealed class NullDataSource : IDataSource
    {
        public static readonly NullDataSource Instance = new();

        public ValueTask<ReadOnlyMemory<byte>?> FetchAsync(string key, CancellationToken cancellationToken)
            => ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
    }
}
