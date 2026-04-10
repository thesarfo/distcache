using System.Collections.Concurrent;
using DistCache.Core;
using FluentAssertions;

namespace DistCache.Tests;

public sealed class LruCacheTests
{
    private static LruCache<string, byte[]> CreateCache(long maxBytes) =>
        new(maxBytes, static b => b.Length);

    [Fact]
    public void TryGet_returns_true_on_hit_and_updates_mru_order()
    {
        LruCache<string, byte[]> cache = CreateCache(100);
        cache.Put("a", [1]);
        cache.Put("b", [2]);

        cache.TryGet("a", out byte[]? fromA).Should().BeTrue();
        fromA.Should().Equal(1);

        cache.Put("c", [3]);
        cache.Put("d", [4]);
        cache.TotalBytes.Should().BeLessOrEqualTo(100);

        cache.TryGet("a", out _).Should().BeTrue("MRU 'a' should survive over LRU peers when space is tight");
    }

    [Fact]
    public void TryGet_returns_false_on_miss()
    {
        LruCache<string, byte[]> cache = CreateCache(50);
        cache.TryGet("nope", out byte[]? v).Should().BeFalse();
        v.Should().BeNull();
    }

    [Fact]
    public void Eviction_removes_lru_when_total_bytes_exceeds_max()
    {
        LruCache<string, byte[]> cache = CreateCache(10);
        cache.Put("first", [1, 2, 3, 4]);
        cache.Put("second", [1, 2, 3, 4]);
        cache.TotalBytes.Should().Be(8);
        cache.Count.Should().Be(2);

        cache.Put("third", [1, 2, 3, 4]);
        cache.Count.Should().Be(2);
        cache.TotalBytes.Should().Be(8);
        cache.TryGet("first", out _).Should().BeFalse("oldest entry should be evicted");
        cache.TryGet("third", out _).Should().BeTrue();
        cache.TryGet("second", out _).Should().BeTrue();
    }

    [Fact]
    public void Put_does_not_store_when_single_entry_exceeds_max_bytes_but_removes_existing_key()
    {
        LruCache<string, byte[]> cache = CreateCache(5);
        cache.Put("k", [1, 2, 3]);
        cache.Count.Should().Be(1);

        cache.Put("k", [1, 2, 3, 4, 5, 6]);
        cache.Count.Should().Be(0);
        cache.TotalBytes.Should().Be(0);
    }

    [Fact]
    public void Remove_and_Clear_empty_the_cache()
    {
        LruCache<string, byte[]> cache = CreateCache(20);
        cache.Put("x", [1]);
        cache.Remove("x").Should().BeTrue();
        cache.Count.Should().Be(0);

        cache.Put("y", [2]);
        cache.Clear();
        cache.Count.Should().Be(0);
        cache.TotalBytes.Should().Be(0);
    }

    [Fact]
    public void Concurrent_mixed_operations_complete_without_errors()
    {
        LruCache<int, byte[]> cache = new(4096, static b => b.Length);
        const int iterations = 2000;
        Parallel.For(0, iterations, i =>
        {
            int key = i % 8;
            byte[] payload = [(byte)(i % 255)];
            cache.Put(key, payload);
            cache.TryGet(key, out _);
            if (i % 7 == 0)
            {
                cache.Remove(key);
            }
        });

        cache.Clear();
        cache.Count.Should().Be(0);
    }

    [Fact]
    public void Parallel_stress_maintains_invariants_under_heavy_contention()
    {
        LruCache<int, byte[]> cache = new(65_536, static b => b.Length);
        var errors = new ConcurrentQueue<Exception>();

        Parallel.For(
            0,
            50_000,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                try
                {
                    int key = i % 64;
                    cache.Put(key, new byte[16]);
                    cache.TryGet(key, out _);
                    if ((i & 1) == 0)
                    {
                        cache.Remove(key);
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            });

        errors.Should().BeEmpty();
        cache.TotalBytes.Should().BeLessOrEqualTo(65_536);
    }

    [Fact]
    public void Constructor_throws_when_max_bytes_not_positive()
    {
        Action act = () => _ = new LruCache<string, byte[]>(0, static b => b.Length);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
