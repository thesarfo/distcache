using System.Threading;
using DistCache.Core.Configuration;
using DistCache.Core.Models;
using DistCache.Core.Routing;
using DistCache.Discovery;
using FluentAssertions;
using Grpc.Net.Client;

namespace DistCache.Tests.Routing;

public sealed class PeerManagerGrpcPoolTests
{
    [Fact]
    public void Pooled_AddPeer_same_endpoint_creates_one_channel()
    {
        int creates = 0;
        var options = new GrpcConnectionPoolOptions
        {
            CreateChannel = (addr, o) =>
            {
                Interlocked.Increment(ref creates);
                return GrpcChannel.ForAddress(new Uri("http://127.0.0.1:9"), o);
            },
        };

        var ring = new ConsistentHashRing(150);
        using var manager = new PeerManager(ring, options);
        var peer = new PeerInfo("id", "127.0.0.1:9");

        manager.AddPeer(peer);
        manager.AddPeer(peer);

        creates.Should().Be(1);
        manager.Peers.Should().ContainSingle().Which.Should().Be("127.0.0.1:9");
        manager.PooledChannels.Should().HaveCount(1);
    }

    [Fact]
    public void Pooled_RemovePeer_clears_channel_from_pool()
    {
        int creates = 0;
        var options = new GrpcConnectionPoolOptions
        {
            CreateChannel = (addr, o) =>
            {
                Interlocked.Increment(ref creates);
                return GrpcChannel.ForAddress(new Uri("http://127.0.0.1:8"), o);
            },
        };

        var ring = new ConsistentHashRing(150);
        var manager = new PeerManager(ring, options);
        manager.AddPeer(new PeerInfo("a", "127.0.0.1:8"));
        manager.PooledChannels.Should().HaveCount(1);
        manager.RemovePeer("127.0.0.1:8");
        manager.PooledChannels.Should().BeEmpty();
        manager.Dispose();
    }

    [Fact]
    public void GrpcPeerAddress_inserts_https_when_missing()
    {
        GrpcPeerAddress.ToAbsoluteUriString("host:1000").Should().Be("https://host:1000");
        GrpcPeerAddress.ToAbsoluteUriString("http://h:1").Should().Be("http://h:1");
    }

    [Fact]
    public async Task StaticDiscovery_StartAsync_tells_PeerManager_to_open_channel()
    {
        int creates = 0;
        var options = new GrpcConnectionPoolOptions
        {
            CreateChannel = (addr, o) =>
            {
                Interlocked.Increment(ref creates);
                return GrpcChannel.ForAddress(new Uri("http://127.0.0.1:7"), o);
            },
        };

        var ring = new ConsistentHashRing(150);
        var manager = new PeerManager(ring, options);
        var discovery = new StaticDiscoveryProvider([new PeerInfo("n", "127.0.0.1:7")]);
        manager.Subscribe(discovery);
        await discovery.StartAsync();
        await Task.Yield();
        await Task.Delay(20);

        creates.Should().Be(1);
        manager.GetClient("127.0.0.1:7").Should().NotBeNull();
        (await discovery.GetPeersAsync()).Should().HaveCount(1);
        manager.Dispose();
    }

    [Fact]
    public async Task StaticDiscovery_SetPeers_emits_leave_and_stops_pooled_peer()
    {
        int creates = 0;
        var options = new GrpcConnectionPoolOptions
        {
            CreateChannel = (addr, o) =>
            {
                Interlocked.Increment(ref creates);
                return GrpcChannel.ForAddress(new Uri("http://127.0.0.1:6"), o);
            },
        };

        var ring = new ConsistentHashRing(150);
        var manager = new PeerManager(ring, options);
        var discovery = new StaticDiscoveryProvider();
        manager.Subscribe(discovery);
        discovery.SetPeers(new[] { new PeerInfo("a", "127.0.0.1:6") });
        await Task.Yield();
        await Task.Delay(20);

        creates.Should().Be(1);
        discovery.SetPeers(Array.Empty<PeerInfo>());
        await Task.Yield();
        await Task.Delay(20);

        manager.PooledChannels.Should().BeEmpty();
        manager.GetClient("127.0.0.1:6").Should().BeNull();
        manager.Dispose();
    }
}
