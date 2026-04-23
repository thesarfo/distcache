using DistCache.Core;
using DistCache.Core.Abstractions;
using DistCache.Core.Configuration;
using DistCache.Core.Models;
using FluentAssertions;
using NSubstitute;

namespace DistCache.Tests;


public sealed class CoreContractsTests
{
    [Fact]
    public void CacheResult_carries_hit_and_miss_shapes()
    {
        byte[] payload = { 1, 2, 3 };
        var hit = new CacheResult(true, payload, "products", "sku-1");
        hit.Found.Should().BeTrue();
        hit.Value.ToArray().Should().Equal(payload);
        hit.KeySpaceName.Should().Be("products");
        hit.Key.Should().Be("sku-1");

        var miss = new CacheResult(false, ReadOnlyMemory<byte>.Empty, "products", "missing");
        miss.Found.Should().BeFalse();
        miss.Value.Length.Should().Be(0);
    }

    [Fact]
    public void CacheStats_exposes_counters()
    {
        var stats = new CacheStats(10, 20, 3, 100, 4096, 2);
        stats.Hits.Should().Be(10);
        stats.Misses.Should().Be(20);
        stats.Evictions.Should().Be(3);
        stats.EntryCount.Should().Be(100);
        stats.ApproximateBytes.Should().Be(4096);
        stats.InFlightRequests.Should().Be(2);
    }

    [Fact]
    public void PeerEventKind_covers_enum_values()
    {
        PeerEventKind.Join.Should().Be(PeerEventKind.Join);
        PeerEventKind.Leave.Should().NotBe(PeerEventKind.Join);
        Enum.GetValues<PeerEventKind>().Should().HaveCount(2);
    }

    [Fact]
    public void PeerInfo_and_PeerEvent_round_trip()
    {
        var peer = new PeerInfo("node-1", "10.0.0.5:7800");
        peer.Id.Should().Be("node-1");
        peer.Endpoint.Should().Be("10.0.0.5:7800");

        var joined = new PeerEvent(PeerEventKind.Join, peer);
        joined.Kind.Should().Be(PeerEventKind.Join);
        joined.Peer.Should().Be(peer);

        var left = new PeerEvent(PeerEventKind.Leave, peer);
        left.Kind.Should().Be(PeerEventKind.Leave);
    }

    [Fact]
    public void KeySpaceOptions_EngineConfig_and_StandaloneConfig_records()
    {
        IDataSource dataSource = Substitute.For<IDataSource>();
        var warm = new[] { "a", "b" };
        var options = new KeySpaceOptions("catalog", 1_000_000, TimeSpan.FromHours(1), dataSource, warm);
        options.Name.Should().Be("catalog");
        options.MaxBytes.Should().Be(1_000_000);
        options.Ttl.Should().Be(TimeSpan.FromHours(1));
        options.DataSource.Should().BeSameAs(dataSource);
        options.WarmKeys.Should().Equal(warm);
        options.SweepInterval.Should().BeNull();

        var standalone = new StandaloneConfig("0.0.0.0:9000", "advertised:9000", "solo-peer");
        var engine = new EngineConfig(128, new[] { options }, standalone);
        engine.VirtualNodeCount.Should().Be(128);
        engine.KeySpaces.Should().ContainSingle().Which.Should().BeSameAs(options);
        engine.Standalone.Should().BeSameAs(standalone);
        standalone.ListenAddress.Should().Be("0.0.0.0:9000");
        standalone.AdvertisedEndpoint.Should().Be("advertised:9000");
        standalone.PeerId.Should().Be("solo-peer");

        var engineNoStandalone = new EngineConfig(null, new[] { options });
        engineNoStandalone.Standalone.Should().BeNull();
    }

    [Fact]
    public void Configuration_records_support_value_equality()
    {
        var a = new StandaloneConfig("l", "a", "x");
        var b = new StandaloneConfig("l", "a", "x");
        (a == b).Should().BeTrue();
        a.Equals((object)b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public async Task IDataSource_can_be_implemented_for_fetch_contract()
    {
        var ds = new FixedDataSource("k1", new byte[] { 9 });
        (await ds.FetchAsync("k1", CancellationToken.None))!.Value.ToArray().Should().Equal(9);
        (await ds.FetchAsync("missing", CancellationToken.None)).Should().BeNull();
    }

    private sealed class FixedDataSource : IDataSource
    {
        private readonly string key;
        private readonly byte[]? value;

        public FixedDataSource(string key, byte[]? value)
        {
            this.key = key;
            this.value = value;
        }

        public ValueTask<ReadOnlyMemory<byte>?> FetchAsync(string k, CancellationToken cancellationToken = default)
        {
            if (k == key && value is not null)
            {
                return new ValueTask<ReadOnlyMemory<byte>?>(new ReadOnlyMemory<byte>(value));
            }

            return new ValueTask<ReadOnlyMemory<byte>?>(result: null);
        }
    }
}
