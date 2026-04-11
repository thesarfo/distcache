using System.Security.Cryptography.X509Certificates;
using System.Text;
using DistCache.Admin.Extensions;
using DistCache.Core.Abstractions;
using DistCache.Core.Configuration;
using DistCache.Core.Engine;
using DistCache.Core.Exceptions;
using DistCache.Core.Networking;
using FluentAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace DistCache.Tests.Networking;

/// <summary>
/// Integration tests verifying mutual TLS between cache nodes.
/// Each test spins up a real Kestrel server on port 0 (OS-assigned) so the full TLS
/// handshake is exercised — <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> bypasses TLS
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

    // One CA and two node certs shared across all tests (generated once per test class instance).
    private readonly X509Certificate2 _trustedCa;
    private readonly X509Certificate2 _nodeCert;

    public CachePeerMtlsTests()
    {
        _trustedCa = TestCertificateAuthority.CreateCa();
        _nodeCert = TestCertificateAuthority.IssueNode(_trustedCa, "distcache-test-node");
    }

    // ── Happy path ────────────────────────────────────────────────────────

    [Fact]
    public async Task MtlsHandshake_with_valid_client_cert_succeeds()
    {
        TlsOptions tls = new()
        {
            NodeCertificate = _nodeCert,
            TrustedCaCertificate = _trustedCa,
        };

        using StandaloneEngine engine = new(new EngineConfig(null, [Options]));
        (WebApplication node, string address) = await StartMtlsNode(tls, engine);

        using GrpcCachePeerClient client = GrpcCachePeerClient.Create(address, tls);

        byte[] payload = Encoding.UTF8.GetBytes("hello-mtls");
        await client.PutAsync(KeySpace, "k1", payload);
        (bool found, ReadOnlyMemory<byte> value) = await client.GetAsync(KeySpace, "k1");

        found.Should().BeTrue();
        value.ToArray().Should().Equal(payload);

        await node.StopAsync();
        await engine.DisposeAsync();
    }

    // ── Rejection: missing client cert ────────────────────────────────────

    [Fact]
    public async Task Missing_client_cert_is_rejected()
    {
        TlsOptions serverTls = new()
        {
            NodeCertificate = _nodeCert,
            TrustedCaCertificate = _trustedCa,
        };

        using StandaloneEngine engine = new(new EngineConfig(null, [Options]));
        (WebApplication node, string address) = await StartMtlsNode(serverTls, engine);

        // Client presents no certificate — only trusts the server's CA.
        TlsOptions clientTls = new()
        {
            TrustedCaCertificate = _trustedCa,
            // No NodeCertificate → no client cert presented
        };

        using GrpcCachePeerClient client = GrpcCachePeerClient.Create(address, clientTls);

        Func<Task> act = () => client.GetAsync(KeySpace, "k");

        await act.Should().ThrowAsync<CacheException>();

        await node.StopAsync();
        await engine.DisposeAsync();
    }

    // ── Rejection: client cert from wrong CA ──────────────────────────────

    [Fact]
    public async Task Client_cert_from_wrong_CA_is_rejected()
    {
        TlsOptions serverTls = new()
        {
            NodeCertificate = _nodeCert,
            TrustedCaCertificate = _trustedCa,
        };

        using StandaloneEngine engine = new(new EngineConfig(null, [Options]));
        (WebApplication node, string address) = await StartMtlsNode(serverTls, engine);

        // Different CA and cert — the server's trusted-CA won't accept it.
        X509Certificate2 rogue = TestCertificateAuthority.IssueNode(
            TestCertificateAuthority.CreateCa(),
            "rogue-node");

        TlsOptions clientTls = new()
        {
            NodeCertificate = rogue,
            TrustedCaCertificate = _trustedCa,
        };

        using GrpcCachePeerClient client = GrpcCachePeerClient.Create(address, clientTls);

        Func<Task> act = () => client.GetAsync(KeySpace, "k");

        await act.Should().ThrowAsync<CacheException>();

        await node.StopAsync();
        await engine.DisposeAsync();
    }

    // ── Dev mode: AllowInsecure bypasses cert requirements ────────────────

    [Fact]
    public async Task AllowInsecure_flag_bypasses_cert_validation()
    {
        TlsOptions serverTls = new()
        {
            NodeCertificate = _nodeCert,
            AllowInsecure = true,
        };

        using StandaloneEngine engine = new(new EngineConfig(null, [Options]));
        (WebApplication node, string address) = await StartMtlsNode(serverTls, engine);

        // Client also skips server cert validation (self-signed in insecure mode).
        TlsOptions clientTls = new() { AllowInsecure = true };
        using GrpcCachePeerClient client = GrpcCachePeerClient.Create(address, clientTls);

        byte[] payload = Encoding.UTF8.GetBytes("insecure-ok");
        await client.PutAsync(KeySpace, "k-dev", payload);
        (bool found, ReadOnlyMemory<byte> value) = await client.GetAsync(KeySpace, "k-dev");

        found.Should().BeTrue();
        value.ToArray().Should().Equal(payload);

        await node.StopAsync();
        await engine.DisposeAsync();
    }

    // ── Helper ────────────────────────────────────────────────────────────

    private static async Task<(WebApplication App, string Address)> StartMtlsNode(
        TlsOptions serverTls,
        StandaloneEngine engine)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        // Bind on a random OS-assigned port on the loopback interface.
        builder.WebHost.UseKestrel(k => k.ListenLocalhost(0, lo => lo.UseHttps()));
        builder.WebHost.UseCachePeerMtls(serverTls);

        builder.Services.AddGrpc();
        builder.Services.AddSingleton<ILocalCacheAccess>(engine);

        WebApplication app = builder.Build();
        app.MapGrpcService<CachePeerServer>();
        await app.StartAsync();

        // Read the actual bound address (port 0 → OS-assigned port).
        IServerAddressesFeature? feature =
            app.Services.GetRequiredService<IServerAddressesFeature>();
        string address = feature?.Addresses.First()
            ?? throw new InvalidOperationException("Could not determine server address after start.");

        return (app, address);
    }

    // ── No-op data source ─────────────────────────────────────────────────

    private sealed class NullDataSource : IDataSource
    {
        public static readonly NullDataSource Instance = new();

        public ValueTask<ReadOnlyMemory<byte>?> FetchAsync(string key, CancellationToken cancellationToken)
            => ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
    }
}
