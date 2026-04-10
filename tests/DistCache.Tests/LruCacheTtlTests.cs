using DistCache.Core;
using DistCache.Core.Lru;
using FluentAssertions;

namespace DistCache.Tests;

public sealed class LruCacheTtlTests
{
    /// <summary>
    /// Wall-clock dependent; 150ms delay gives margin over 100ms TTL on typical CI runners.
    /// </summary>
    [Fact]
    public async Task TryGet_returns_miss_after_ttl_and_removes_entry()
    {
        await using var cache = new LruCache<string, byte[]>(
            maxBytes: 4096,
            getByteSize: static b => b.Length,
            keyComparer: null,
            entryTimeToLive: TimeSpan.FromMilliseconds(100),
            sweepInterval: null);

        cache.Put("k", [1, 2, 3]);
        cache.TryGet("k", out _).Should().BeTrue();
        cache.Count.Should().Be(1);

        await Task.Delay(150);

        cache.TryGet("k", out byte[]? value).Should().BeFalse();
        value.Should().BeNull();
        cache.Count.Should().Be(0);
    }

    [Fact]
    public async Task TryGet_hits_immediately_after_put_when_ttl_not_elapsed()
    {
        await using var cache = new LruCache<string, byte[]>(
            4096,
            static b => b.Length,
            null,
            TimeSpan.FromSeconds(60),
            null);

        cache.Put("k", [9]);
        cache.TryGet("k", out byte[]? v).Should().BeTrue();
        v.Should().Equal(9);
    }

    [Fact]
    public async Task Background_sweep_can_remove_expired_entry_without_try_get()
    {
        await using var cache = new LruCache<string, byte[]>(
            4096,
            static b => b.Length,
            null,
            entryTimeToLive: TimeSpan.FromMilliseconds(80),
            sweepInterval: TimeSpan.FromMilliseconds(30));

        cache.Put("solo", [1]);
        await Task.Delay(200);

        cache.Count.Should().Be(0);
    }

    [Fact]
    public void Constructor_throws_when_sweep_interval_set_without_entry_ttl()
    {
        Action act = () => _ = new LruCache<string, byte[]>(
            100,
            static b => b.Length,
            null,
            entryTimeToLive: null,
            sweepInterval: TimeSpan.FromSeconds(1));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_throws_when_sweep_interval_not_positive()
    {
        Action act = () => _ = new LruCache<string, byte[]>(
            100,
            static b => b.Length,
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
