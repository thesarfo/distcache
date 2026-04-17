using System.Text;
using DistCache.Core.Abstractions;
using DistCache.Core.Configuration;
using DistCache.Core.Engine;
using DistCache.Core.Exceptions;
using DistCache.Core.Models;
using DistCache.Core.Networking;
using DistCache.Core.Routing;
using FluentAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DistCache.Tests.Engine;

/// <summary>
/// Integration tests for <see cref="DistributedEngine"/>: 2-node routing and peer-failure fallback.
/// Node B runs as an in-process gRPC <see cref="CachePeerServer"/> on a <see cref="TestServer"/>.
/// Node A is a <see cref="DistributedEngine"/> whose <see cref="PeerManager"/> holds a gRPC client
/// pointing at node B's test server.
/// </summary>
public sealed class DistributedEngineTests : IAsyncDisposable
{
    private const string KeySpace = "dist-ks";
    private const string EndpointA = "node-a";
    private const string EndpointB = "node-b";

    private static readonly KeySpaceOptions Options = new(
        Name: KeySpace,
        MaxBytes: 1024 * 1024,
        Ttl: null,
        DataSource: NullDataSource.Instance,
        WarmKeys: []);

    // Node B: standalone engine + in-process gRPC server (TestServer).
    private readonly StandaloneEngine engineB;
    private readonly WebApplication nodeB;

    // Node A: DistributedEngine whose ring contains both "node-a" (self) and "node-b" (remote).
    // PeerManager owns the GrpcCachePeerClient for node B and will dispose it on DisposeAsync.
    private readonly DistributedEngine distributedEngineA;
    private readonly ConsistentHashRing ringA;

    public DistributedEngineTests()
    {
        engineB = new StandaloneEngine(new EngineConfig(null, [Options]));
        nodeB = BuildGrpcNode(engineB);
        nodeB.StartAsync().GetAwaiter().GetResult();

        ringA = new ConsistentHashRing(replicaCount: 150);

        // PeerManager owns the client; the factory is called once for "node-b".
        PeerManager peerManagerA = new(
            ringA,
            _ => GrpcCachePeerClient.Create(new Uri("http://localhost"), nodeB.GetTestClient()));

        distributedEngineA = new DistributedEngine(
            new EngineConfig(null, [Options], new StandaloneConfig(string.Empty, EndpointA)),
            peerManagerA);

        // Register the remote peer — this adds "node-b" to ringA and creates the gRPC client.
        peerManagerA.AddPeer(new PeerInfo("node-b", EndpointB));
    }

    // ── Routing: node A forwards operations for keys owned by node B ─────────

    [Fact]
    public async Task Put_for_key_owned_by_nodeB_stores_value_on_nodeB()
    {
        string key = FindKeyFor(ringA, EndpointB);
        byte[] payload = Encoding.UTF8.GetBytes("routed-put");

        await distributedEngineA.PutAsync(KeySpace, key, payload);

        bool found = ((ILocalCacheAccess)engineB).TryGetLocal(KeySpace, key, out byte[]? stored);
        found.Should().BeTrue("engine A should forward Put to the owning peer B");
        stored.Should().Equal(payload);
    }

    [Fact]
    public async Task Get_for_key_owned_by_nodeB_returns_value_from_nodeB()
    {
        string key = FindKeyFor(ringA, EndpointB);
        byte[] payload = Encoding.UTF8.GetBytes("value-on-b");

        ((ILocalCacheAccess)engineB).PutLocal(KeySpace, key, payload);

        CacheResult result = await distributedEngineA.GetAsync(KeySpace, key);

        result.Found.Should().BeTrue("engine A should retrieve the value from owning peer B");
        result.Value.ToArray().Should().Equal(payload);
    }

    [Fact]
    public async Task Delete_for_key_owned_by_nodeB_removes_it_from_nodeB()
    {
        string key = FindKeyFor(ringA, EndpointB);

        ((ILocalCacheAccess)engineB).PutLocal(KeySpace, key, Encoding.UTF8.GetBytes("to-delete"));
        await distributedEngineA.DeleteAsync(KeySpace, key);

        ((ILocalCacheAccess)engineB).TryGetLocal(KeySpace, key, out _)
            .Should().BeFalse("engine A should forward Delete to the owning peer B");
    }

    [Fact]
    public async Task Key_owned_by_nodeA_is_served_locally_without_touching_nodeB()
    {
        string key = FindKeyFor(ringA, EndpointA);
        byte[] payload = Encoding.UTF8.GetBytes("local-value");

        await distributedEngineA.PutAsync(KeySpace, key, payload);
        CacheResult result = await distributedEngineA.GetAsync(KeySpace, key);

        result.Found.Should().BeTrue("keys owned by self should be stored and retrieved locally");
        result.Value.ToArray().Should().Equal(payload);

        // Node B must not have the value — the write never left node A.
        ((ILocalCacheAccess)engineB).TryGetLocal(KeySpace, key, out _)
            .Should().BeFalse("a locally-owned key must not be forwarded to peer B");
    }

    // ── Fallback: Unavailable peer triggers local data-source read-through ────

    [Fact]
    public async Task Get_falls_back_to_data_source_when_owning_peer_is_unavailable()
    {
        byte[] fallbackData = Encoding.UTF8.GetBytes("from-data-source");

        IDataSource dataSource = Substitute.For<IDataSource>();
        dataSource
            .FetchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<byte>(fallbackData));

        ICachePeerClient unavailableClient = Substitute.For<ICachePeerClient>();
        unavailableClient
            .GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(bool Found, ReadOnlyMemory<byte> Value)>(
                new CacheException("simulated peer down", StatusCode.Unavailable)));

        KeySpaceOptions optionsWithSource = new(
            Name: "fallback-ks",
            MaxBytes: 1024 * 1024,
            Ttl: null,
            DataSource: dataSource,
            WarmKeys: []);

        ConsistentHashRing ring = new(replicaCount: 150);
        PeerManager pm = new(ring, _ => unavailableClient);

        await using DistributedEngine engine = new(
            new EngineConfig(null, [optionsWithSource], new StandaloneConfig(string.Empty, "self")),
            pm);

        pm.AddPeer(new PeerInfo("remote", "remote"));

        string key = FindKeyFor(ring, "remote");

        CacheResult result = await engine.GetAsync("fallback-ks", key);

        result.Found.Should().BeTrue("the engine should fall back to the data source on Unavailable");
        result.Value.ToArray().Should().Equal(fallbackData);
        await dataSource.Received(1).FetchAsync(key, Arg.Any<CancellationToken>());
    }

    // ── Topology: discovery join event wires new peer into routing ────────────

    [Fact]
    public async Task Join_event_via_discovery_routes_keys_to_newly_joined_peer()
    {
        ConsistentHashRing ring = new(replicaCount: 150);

        // PeerManager starts empty; a Discovery join event will add "node-b".
        PeerManager pm = new(
            ring,
            _ => GrpcCachePeerClient.Create(new Uri("http://localhost"), nodeB.GetTestClient()));

        var subject = new SimpleSubject<PeerEvent>();
        IDiscoveryProvider discovery = Substitute.For<IDiscoveryProvider>();
        discovery.PeerChanges.Returns(subject);
        pm.Subscribe(discovery);

        await using DistributedEngine engine = new(
            new EngineConfig(null, [Options], new StandaloneConfig(string.Empty, "solo")),
            pm);

        // Emit the join event — PeerManager adds "node-b" to the ring.
        subject.OnNext(new PeerEvent(PeerEventKind.Join, new PeerInfo("node-b", EndpointB)));

        string key = FindKeyFor(ring, EndpointB);
        byte[] payload = Encoding.UTF8.GetBytes("value-after-join");
        ((ILocalCacheAccess)engineB).PutLocal(KeySpace, key, payload);

        CacheResult result = await engine.GetAsync(KeySpace, key);

        result.Found.Should().BeTrue("engine should route to the newly joined peer B after the join event");
        result.Value.ToArray().Should().Equal(payload);
    }

    // ── Disposal ─────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        // distributedEngineA.DisposeAsync disposes PeerManager which disposes the gRPC client.
        await distributedEngineA.DisposeAsync();
        await nodeB.StopAsync();
        await engineB.DisposeAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static WebApplication BuildGrpcNode(StandaloneEngine engine)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<ILocalCacheAccess>(engine);
        WebApplication app = builder.Build();
        app.MapGrpcService<CachePeerServer>();
        return app;
    }

    private static string FindKeyFor(ConsistentHashRing ring, string endpoint)
    {
        for (int i = 0; i < 100_000; i++)
        {
            string key = $"test-key-{i}";
            if (string.Equals(ring.GetOwner(key), endpoint, StringComparison.Ordinal))
            {
                return key;
            }
        }

        throw new InvalidOperationException(
            $"Could not find a key that hashes to '{endpoint}' in 100 000 attempts.");
    }

    private sealed class NullDataSource : IDataSource
    {
        public static readonly NullDataSource Instance = new();

        public ValueTask<ReadOnlyMemory<byte>?> FetchAsync(string key, CancellationToken cancellationToken)
            => ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
    }
}
