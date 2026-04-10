using DistCache.Core;
using FluentAssertions;
using NSubstitute;

namespace DistCache.Tests;

public sealed class StandaloneEngineTests
{
    private static EngineConfig Config(string ksName, long maxBytes, IDataSource ds, TimeSpan? ttl = null, TimeSpan? sweep = null, params string[] warmKeys) =>
        new(null, new[] { new KeySpaceOptions(ksName, maxBytes, ttl, ds, warmKeys.ToList(), sweep) });

    [Fact]
    public async Task GetAsync_hits_after_put_without_calling_data_source()
    {
        IDataSource ds = Substitute.For<IDataSource>();
        await using var engine = new StandaloneEngine(Config("ks", 10_000, ds));

        await engine.PutAsync("ks", "a", new byte[] { 1, 2 }, CancellationToken.None);
        CacheResult r = await engine.GetAsync("ks", "a");

        r.Found.Should().BeTrue();
        r.Value.ToArray().Should().Equal(1, 2);
        await ds.DidNotReceive().FetchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_miss_fetches_once_then_hits_lru()
    {
        IDataSource ds = Substitute.For<IDataSource>();
        ds.FetchAsync("x", Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<ReadOnlyMemory<byte>?>(new byte[] { 7, 8 }));

        await using var engine = new StandaloneEngine(Config("ks", 10_000, ds));

        CacheResult first = await engine.GetAsync("ks", "x");
        first.Found.Should().BeTrue();
        first.Value.ToArray().Should().Equal(7, 8);

        CacheResult second = await engine.GetAsync("ks", "x");
        second.Found.Should().BeTrue();

        await ds.Received(1).FetchAsync("x", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_after_ttl_expiry_fetches_again()
    {
        IDataSource ds = Substitute.For<IDataSource>();
        ds.FetchAsync("e", Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<ReadOnlyMemory<byte>?>(new byte[] { 1 }));

        await using var engine = new StandaloneEngine(Config("ks", 10_000, ds, TimeSpan.FromMilliseconds(100), null));

        await engine.GetAsync("ks", "e");
        await Task.Delay(150);

        await engine.GetAsync("ks", "e");

        await ds.Received(2).FetchAsync("e", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Eviction_removes_oldest_to_stay_under_max_bytes()
    {
        IDataSource ds = Substitute.For<IDataSource>();
        ds.FetchAsync("a", Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<ReadOnlyMemory<byte>?>(new byte[4]));
        ds.FetchAsync("b", Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<ReadOnlyMemory<byte>?>(new byte[4]));
        ds.FetchAsync("c", Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<ReadOnlyMemory<byte>?>(new byte[4]));

        await using var engine = new StandaloneEngine(Config("ks", 10, ds));

        await engine.GetAsync("ks", "a");
        await engine.GetAsync("ks", "b");
        await engine.GetAsync("ks", "c");

        CacheResult ra = await engine.GetAsync("ks", "a");
        ra.Found.Should().BeTrue();
        await ds.Received(2).FetchAsync("a", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Batch_put_get_delete_roundtrip()
    {
        IDataSource ds = Substitute.For<IDataSource>();
        await using var engine = new StandaloneEngine(Config("ks", 10_000, ds));

        await engine.PutManyAsync(
            "ks",
            new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
            {
                ["p"] = new byte[] { 1 },
                ["q"] = new byte[] { 2, 3 },
            },
            CancellationToken.None);

        IReadOnlyDictionary<string, CacheResult> many = await engine.GetManyAsync("ks", new[] { "p", "q", "missing" });
        many["p"].Found.Should().BeTrue();
        many["p"].Value.ToArray().Should().Equal(1);
        many["q"].Found.Should().BeTrue();
        many["missing"].Found.Should().BeFalse();

        await engine.DeleteManyAsync("ks", new[] { "p", "q" }, CancellationToken.None);
        (await engine.GetAsync("ks", "p")).Found.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_allows_second_dispose()
    {
        IDataSource ds = Substitute.For<IDataSource>();
        var engine = new StandaloneEngine(Config("ks", 10_000, ds));
        await engine.DisposeAsync();
        await engine.DisposeAsync();
    }
}
