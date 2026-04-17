using DistCache.Core.Routing;
using FluentAssertions;

namespace DistCache.Tests.Routing;

public sealed class ConsistentHashRingTests
{
  

    [Fact]
    public void GetOwner_returns_null_for_empty_ring()
    {
        var ring = new ConsistentHashRing();
        ring.GetOwner("any-key").Should().BeNull();
    }

    [Fact]
    public void GetOwner_returns_sole_peer_for_every_key()
    {
        var ring = new ConsistentHashRing();
        ring.AddPeer("peer-A");

        ring.GetOwner("foo").Should().Be("peer-A");
        ring.GetOwner("bar").Should().Be("peer-A");
        ring.GetOwner(string.Empty).Should().Be("peer-A");
    }


    [Fact]
    public void Determinism_same_key_always_maps_to_same_peer()
    {
        var ring = new ConsistentHashRing(replicaCount: 150);
        ring.AddPeer("peer-A");
        ring.AddPeer("peer-B");
        ring.AddPeer("peer-C");

        for (int i = 0; i < 1_000; i++)
        {
            string key = $"key-{i}";
            string? first = ring.GetOwner(key);
            ring.GetOwner(key).Should().Be(first, because: $"key '{key}' must always map to the same peer");
        }
    }

    [Fact]
    public void Determinism_insertion_order_does_not_affect_routing()
    {
        string[] peers = ["peer-A", "peer-B", "peer-C"];

        var ring1 = new ConsistentHashRing(replicaCount: 150);
        foreach (string p in peers)
        {
            ring1.AddPeer(p);
        }

        var ring2 = new ConsistentHashRing(replicaCount: 150);
        foreach (string p in peers.Reverse())
        {
            ring2.AddPeer(p);
        }

        for (int i = 0; i < 1_000; i++)
        {
            string key = $"key-{i}";
            ring1.GetOwner(key).Should().Be(ring2.GetOwner(key),
                because: "ring position depends only on peer set, not insertion order");
        }
    }


    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void Distribution_is_within_15_percent_of_uniform(int peerCount)
    {
        var ring = new ConsistentHashRing(replicaCount: 150);
        for (int i = 0; i < peerCount; i++)
        {
            ring.AddPeer($"10.0.0.{i}:5000");
        }

        const int keyCount = 10_000;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < keyCount; i++)
        {
            string owner = ring.GetOwner($"benchmark-key-{i}")!;
            counts[owner] = counts.TryGetValue(owner, out int c) ? c + 1 : 1;
        }

        double expected = (double)keyCount / peerCount;
        int lo = (int)(expected * 0.85);
        int hi = (int)(expected * 1.15);

        counts.Should().HaveCount(peerCount, because: "every peer should receive at least one key");
        foreach (KeyValuePair<string, int> kv in counts)
        {
            kv.Value.Should().BeInRange(lo, hi,
                because: $"peer {kv.Key} should receive ≈1/{peerCount} of keys (±15 %)");
        }
    }


    [Fact]
    public void AddPeer_to_empty_ring_produces_no_diffs()
    {
        var ring = new ConsistentHashRing();
        ring.AddPeer("peer-A").Should().BeEmpty();
    }

    [Fact]
    public void AddPeer_second_peer_produces_diffs_all_from_first_peer()
    {
        var ring = new ConsistentHashRing(replicaCount: 150);
        ring.AddPeer("peer-A");
        IReadOnlyList<OwnershipDiff> diffs = ring.AddPeer("peer-B");

        diffs.Should().NotBeEmpty();
        diffs.Should().AllSatisfy(d =>
        {
            d.PreviousOwner.Should().Be("peer-A");
            d.NewOwner.Should().Be("peer-B");
        });
    }

    [Fact]
    public void AddPeer_same_peer_twice_is_idempotent()
    {
        var ring = new ConsistentHashRing();
        ring.AddPeer("peer-A");
        ring.AddPeer("peer-A").Should().BeEmpty();
        ring.Peers.Should().ContainSingle();
    }

    [Fact]
    public void AddPeer_increases_virtual_node_count_by_replica_count()
    {
        var ring = new ConsistentHashRing(replicaCount: 10);
        ring.AddPeer("peer-A");
        // Some hash positions may collide, so count ≤ replicaCount.
        ring.VirtualNodeCount.Should().BePositive().And.BeLessOrEqualTo(10);
    }



    [Fact]
    public void RemovePeer_nonexistent_peer_is_idempotent()
    {
        var ring = new ConsistentHashRing();
        ring.RemovePeer("ghost").Should().BeEmpty();
    }

    [Fact]
    public void RemovePeer_sole_peer_empties_ring_and_produces_no_diffs()
    {
        var ring = new ConsistentHashRing();
        ring.AddPeer("peer-A");
        ring.RemovePeer("peer-A").Should().BeEmpty();
        ring.GetOwner("any-key").Should().BeNull();
        ring.Peers.Should().BeEmpty();
    }

    [Fact]
    public void RemovePeer_transfers_ownership_to_surviving_peer()
    {
        var ring = new ConsistentHashRing(replicaCount: 150);
        ring.AddPeer("peer-A");
        ring.AddPeer("peer-B");
        IReadOnlyList<OwnershipDiff> diffs = ring.RemovePeer("peer-B");

        diffs.Should().NotBeEmpty();
        diffs.Should().AllSatisfy(d =>
        {
            d.PreviousOwner.Should().Be("peer-B");
            d.NewOwner.Should().Be("peer-A");
        });
    }



    [Fact]
    public void OwnershipDiff_matches_actual_ownership_changes_when_peer_joins()
    {
        var ring = new ConsistentHashRing(replicaCount: 150);
        ring.AddPeer("peer-A");

        const int sampleSize = 5_000;
        var before = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < sampleSize; i++)
        {
            string key = $"key-{i}";
            before[key] = ring.GetOwner(key)!;
        }

        IReadOnlyList<OwnershipDiff> diffs = ring.AddPeer("peer-B");

        // Every key that changed owner must be covered by a (prev → new) diff entry.
        for (int i = 0; i < sampleSize; i++)
        {
            string key = $"key-{i}";
            string newOwner = ring.GetOwner(key)!;
            string prevOwner = before[key];

            if (newOwner != prevOwner)
            {
                diffs.Should().Contain(
                    d => d.PreviousOwner == prevOwner && d.NewOwner == newOwner,
                    because: $"key '{key}' moved from {prevOwner} to {newOwner}");
            }
        }
    }

    [Fact]
    public void OwnershipDiff_matches_actual_ownership_changes_when_peer_leaves()
    {
        var ring = new ConsistentHashRing(replicaCount: 150);
        ring.AddPeer("peer-A");
        ring.AddPeer("peer-B");

        const int sampleSize = 5_000;
        var before = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < sampleSize; i++)
        {
            string key = $"key-{i}";
            before[key] = ring.GetOwner(key)!;
        }

        IReadOnlyList<OwnershipDiff> diffs = ring.RemovePeer("peer-B");

        for (int i = 0; i < sampleSize; i++)
        {
            string key = $"key-{i}";
            string newOwner = ring.GetOwner(key)!;
            string prevOwner = before[key];

            if (newOwner != prevOwner)
            {
                diffs.Should().Contain(
                    d => d.PreviousOwner == prevOwner && d.NewOwner == newOwner,
                    because: $"key '{key}' moved from {prevOwner} to {newOwner}");
            }
        }
    }

    [Fact]
    public void Keys_not_covered_by_any_diff_do_not_change_owner_on_add()
    {
        var ring = new ConsistentHashRing(replicaCount: 150);
        ring.AddPeer("peer-A");

        const int sampleSize = 5_000;
        var before = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < sampleSize; i++)
        {
            string key = $"key-{i}";
            before[key] = ring.GetOwner(key)!;
        }

        IReadOnlyList<OwnershipDiff> diffs = ring.AddPeer("peer-B");
        var movedPairs = new HashSet<(string, string)>(
            diffs.Select(d => (d.PreviousOwner, d.NewOwner)));

        for (int i = 0; i < sampleSize; i++)
        {
            string key = $"key-{i}";
            string newOwner = ring.GetOwner(key)!;
            string prevOwner = before[key];

            if (!movedPairs.Contains((prevOwner, newOwner)))
            {
                newOwner.Should().Be(prevOwner,
                    because: $"key '{key}' has no covering diff so its owner must not change");
            }
        }
    }


    [Fact]
    public void ReplicaCount_is_preserved()
    {
        var ring = new ConsistentHashRing(replicaCount: 42);
        ring.ReplicaCount.Should().Be(42);
    }

    [Fact]
    public void Constructor_throws_for_zero_or_negative_replica_count()
    {
        Action zero = () => _ = new ConsistentHashRing(0);
        Action negative = () => _ = new ConsistentHashRing(-1);

        zero.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetOwner_throws_for_null_key()
    {
        var ring = new ConsistentHashRing();
        Action act = () => ring.GetOwner(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
