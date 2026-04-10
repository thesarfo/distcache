using DistCache.Core;
using DistCache.Core.Registry;
using FluentAssertions;
using NSubstitute;

namespace DistCache.Tests;

public sealed class KeySpaceRegistryTests
{
    private static KeySpaceOptions Options(string name, long maxBytes) =>
        new(name, maxBytes, null, Substitute.For<IDataSource>(), Array.Empty<string>());

    [Fact]
    public void Register_routes_to_distinct_local_caches()
    {
        var registry = new KeySpaceRegistry();
        registry.Register(Options("a", 10_000));
        registry.Register(Options("b", 10_000));

        registry.TryGet("a", out KeySpaceEntry? ea).Should().BeTrue();
        registry.TryGet("b", out KeySpaceEntry? eb).Should().BeTrue();

        ea!.PutLocal("same-key", [1, 2]);
        eb!.PutLocal("same-key", [9, 9, 9]);

        ea.TryGetLocal("same-key", out byte[]? va).Should().BeTrue();
        va.Should().Equal(1, 2);

        eb.TryGetLocal("same-key", out byte[]? vb).Should().BeTrue();
        vb.Should().Equal(9, 9, 9);
    }

    [Fact]
    public void Register_throws_when_name_already_exists()
    {
        var registry = new KeySpaceRegistry();
        registry.Register(Options("dup", 100));
        Action act = () => registry.Register(Options("dup", 200));
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_throws_when_name_is_empty_or_whitespace(string name)
    {
        var registry = new KeySpaceRegistry();
        Action act = () => registry.Register(new KeySpaceOptions(name, 100, null, Substitute.For<IDataSource>(), Array.Empty<string>()));
        act.Should().Throw<ArgumentException>().WithParameterName("name");
    }

    [Fact]
    public void Update_replaces_definition_and_swaps_lru_data_is_cleared()
    {
        IDataSource ds1 = Substitute.For<IDataSource>();
        IDataSource ds2 = Substitute.For<IDataSource>();

        var registry = new KeySpaceRegistry();
        registry.Register(new KeySpaceOptions("ks", 5000, null, ds1, Array.Empty<string>()));

        registry.TryGet("ks", out KeySpaceEntry? entry).Should().BeTrue();
        entry!.PutLocal("k", [5, 5, 5]);
        entry.TryGetLocal("k", out _).Should().BeTrue();

        registry.Update(new KeySpaceOptions("ks", 2000, null, ds2, Array.Empty<string>()));

        entry.Definition.DataSource.Should().BeSameAs(ds2);
        entry.TryGetLocal("k", out _).Should().BeFalse();
    }

    [Fact]
    public void Update_throws_when_key_space_missing()
    {
        var registry = new KeySpaceRegistry();
        Action act = () => registry.Update(Options("missing", 100));
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public async Task UnregisterAsync_removes_entry_and_disposes_cache()
    {
        var registry = new KeySpaceRegistry();
        registry.Register(Options("x", 1000));
        registry.TryGet("x", out _).Should().BeTrue();

        bool removed = await registry.UnregisterAsync("x");
        removed.Should().BeTrue();
        registry.TryGet("x", out _).Should().BeFalse();
    }

    [Fact]
    public void Concurrent_reads_and_update_do_not_throw()
    {
        var registry = new KeySpaceRegistry();
        registry.Register(Options("hot", 50_000));

        Parallel.For(0, 500, i =>
        {
            if (i == 100)
            {
                registry.Update(new KeySpaceOptions("hot", 40_000, null, Substitute.For<IDataSource>(), Array.Empty<string>()));
            }
            else
            {
                if (registry.TryGet("hot", out KeySpaceEntry? e) && e is not null)
                {
                    e.TryGetLocal("k", out _);
                    e.PutLocal("k", [1]);
                }
            }
        });
    }
}
