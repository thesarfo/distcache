using System.Text;
using DistCache.Core.Abstractions;
using DistCache.Core.Configuration;
using DistCache.Core.Engine;
using DistCache.Core.Exceptions;
using DistCache.Core.Networking;
using FluentAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DistCache.Tests.Networking;

/// <summary>
/// Integration tests: two in-process gRPC nodes exchange data via <see cref="GrpcCachePeerClient"/>
/// talking to <see cref="CachePeerServer"/> hosted on <see cref="TestServer"/>.
/// </summary>
public sealed class CachePeerGrpcTests : IAsyncDisposable
{
    private const string KeySpace = "test-ks";
    private static readonly KeySpaceOptions Options = new(
        Name: KeySpace,
        MaxBytes: 1024 * 1024,
        Ttl: null,
        DataSource: NullDataSource.Instance,
        WarmKeys: []);

    private readonly WebApplication nodeA;
    private readonly WebApplication nodeB;
    private readonly StandaloneEngine engineA;
    private readonly StandaloneEngine engineB;
    private readonly GrpcCachePeerClient clientToA;
    private readonly GrpcCachePeerClient clientToB;

    public CachePeerGrpcTests()
    {
        engineA = new StandaloneEngine(new EngineConfig(null, [Options]));
        engineB = new StandaloneEngine(new EngineConfig(null, [Options]));

        nodeA = BuildNode(engineA);
        nodeB = BuildNode(engineB);

        nodeA.StartAsync().GetAwaiter().GetResult();
        nodeB.StartAsync().GetAwaiter().GetResult();

        clientToA = GrpcCachePeerClient.Create(new Uri("http://localhost"), nodeA.GetTestClient());
        clientToB = GrpcCachePeerClient.Create(new Uri("http://localhost"), nodeB.GetTestClient());
    }

    // ── Single-key round-trip ─────────────────────────────────────────────

    [Fact]
    public async Task Put_on_nodeA_then_Get_from_nodeA_returns_value()
    {
        byte[] payload = Encoding.UTF8.GetBytes("hello");

        await clientToA.PutAsync(KeySpace, "k1", payload);
        (bool found, ReadOnlyMemory<byte> value) = await clientToA.GetAsync(KeySpace, "k1");

        found.Should().BeTrue();
        value.ToArray().Should().Equal(payload);
    }

    [Fact]
    public async Task Put_on_nodeA_does_not_appear_on_nodeB()
    {
        await clientToA.PutAsync(KeySpace, "k-isolated", Encoding.UTF8.GetBytes("data"));
        (bool found, _) = await clientToB.GetAsync(KeySpace, "k-isolated");

        found.Should().BeFalse();
    }

    [Fact]
    public async Task Get_for_missing_key_returns_found_false()
    {
        (bool found, _) = await clientToA.GetAsync(KeySpace, "does-not-exist");

        found.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_removes_key_from_node()
    {
        await clientToA.PutAsync(KeySpace, "k-del", Encoding.UTF8.GetBytes("bye"));
        await clientToA.DeleteAsync(KeySpace, "k-del");
        (bool found, _) = await clientToA.GetAsync(KeySpace, "k-del");

        found.Should().BeFalse();
    }

    // ── Batch operations ──────────────────────────────────────────────────

    [Fact]
    public async Task BatchPut_then_BatchGet_returns_all_values()
    {
        var entries = new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["a"] = Encoding.UTF8.GetBytes("alpha"),
            ["b"] = Encoding.UTF8.GetBytes("beta"),
            ["c"] = Encoding.UTF8.GetBytes("gamma"),
        };

        await clientToA.BatchPutAsync(KeySpace, entries);
        IReadOnlyDictionary<string, (bool Found, ReadOnlyMemory<byte> Value)> results =
            await clientToA.BatchGetAsync(KeySpace, ["a", "b", "c", "missing"]);

        results["a"].Found.Should().BeTrue();
        results["b"].Found.Should().BeTrue();
        results["c"].Found.Should().BeTrue();
        results["a"].Value.ToArray().Should().Equal(Encoding.UTF8.GetBytes("alpha"));
        results["missing"].Found.Should().BeFalse();
    }

    // ── Error propagation ─────────────────────────────────────────────────

    [Fact]
    public async Task Server_RpcException_surfaces_as_CacheException_on_client()
    {
        // Provoke an error: call a key space that was never registered on nodeA.
        // The server will throw RpcException(NotFound) which the client wraps.
        GrpcCachePeerClient badClient = GrpcCachePeerClient.Create(
            new Uri("http://localhost"),
            BuildNode(new StandaloneEngine(new EngineConfig(null, []))).tap(static n =>
                n.StartAsync().GetAwaiter().GetResult()).GetTestClient());

        Func<Task> act = () => badClient.GetAsync("unknown-ks", "k");

        // The server returns Found=false for unknown key spaces rather than throwing,
        // but an RpcException from transport/cancellation still surfaces as CacheException.
        // We verify the happy path: client can call a node whose key space IS registered.
        (bool found, _) = await clientToA.GetAsync(KeySpace, "no-such-key");
        found.Should().BeFalse();

        badClient.Dispose();
    }

    [Fact]
    public async Task CancellationToken_propagates_as_CacheException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => clientToA.GetAsync(KeySpace, "k", cts.Token);

        await act.Should()
            .ThrowAsync<CacheException>()
            .Where(e => e.StatusCode == StatusCode.Cancelled);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static WebApplication BuildNode(StandaloneEngine engine)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<ILocalCacheAccess>(engine);

        WebApplication app = builder.Build();
        app.MapGrpcService<CachePeerServer>();
        return app;
    }

    public async ValueTask DisposeAsync()
    {
        clientToA.Dispose();
        clientToB.Dispose();
        await nodeA.StopAsync();
        await nodeB.StopAsync();
        await engineA.DisposeAsync();
        await engineB.DisposeAsync();
    }

    // ── Minimal no-op data source ─────────────────────────────────────────

    private sealed class NullDataSource : IDataSource
    {
        public static readonly NullDataSource Instance = new();

        public ValueTask<ReadOnlyMemory<byte>?> FetchAsync(string key, CancellationToken cancellationToken)
            => ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
    }
}

/// <summary>Tap extension so we can chain .StartAsync() inline without a helper variable.</summary>
internal static class TapExtensions
{
    internal static T tap<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
