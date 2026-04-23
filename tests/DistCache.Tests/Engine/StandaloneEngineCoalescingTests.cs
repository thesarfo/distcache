using System.Collections.Concurrent;
using DistCache.Core.Abstractions;
using DistCache.Core.Configuration;
using DistCache.Core.Engine;
using DistCache.Core.Models;
using FluentAssertions;
using NSubstitute;

namespace DistCache.Tests.Engine;

public sealed class StandaloneEngineCoalescingTests
{
    private const string KeySpace = "coh-ks";

    private static EngineConfig Config(IDataSource ds) =>
        new(null, new[] { new KeySpaceOptions(KeySpace, 10_000, null, ds, []) });

    [Fact]
    public async Task Concurrent_misses_on_same_key_call_FetchAsync_once()
    {
        IDataSource ds = Substitute.For<IDataSource>();
        ds.FetchAsync("same-key", Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<ReadOnlyMemory<byte>?>(new byte[] { 9, 9, 9 }));

        await using var engine = new StandaloneEngine(Config(ds));

        var tasks = Enumerable.Range(0, 50).Select(_ =>
            engine.GetAsync(KeySpace, "same-key").AsTask()).ToArray();

        await Task.WhenAll(tasks);

        await ds.Received(1).FetchAsync("same-key", Arg.Any<CancellationToken>());
        foreach (Task<CacheResult> t in tasks)
        {
            CacheResult r = await t;
            r.Found.Should().BeTrue();
            r.Value.ToArray().Should().Equal(9, 9, 9);
        }
    }

    [Fact]
    public async Task InFlightRequests_spikes_during_blocked_fetch_then_zero()
    {
        IDataSource ds = Substitute.For<IDataSource>();
        var gate = new TaskCompletionSource<ReadOnlyMemory<byte>?>(TaskCreationOptions.RunContinuationsAsynchronously);

        ds.FetchAsync("block", Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<ReadOnlyMemory<byte>?>(gate.Task));

        await using var engine = new StandaloneEngine(Config(ds));

        Task<CacheResult>[] tasks = Enumerable.Range(0, 30)
            .Select(_ => engine.GetAsync(KeySpace, "block").AsTask())
            .ToArray();

        for (int i = 0; i < 200; i++)
        {
            if (engine.GetCacheStats().InFlightRequests >= 1)
            {
                break;
            }

            await Task.Delay(5);
        }

        engine.GetCacheStats().InFlightRequests.Should().BeGreaterThan(0);

        gate.SetResult(new byte[] { 1 });

        await Task.WhenAll(tasks);

        engine.GetCacheStats().InFlightRequests.Should().Be(0);
        await ds.Received(1).FetchAsync("block", Arg.Any<CancellationToken>());
    }
}
