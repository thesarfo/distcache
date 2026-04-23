using System.Collections.Concurrent;
using DistCache.Core.Concurrency;
using FluentAssertions;

namespace DistCache.Tests.Concurrency;

public sealed class AsyncSingleFlightTests
{
    [Fact]
    public async Task Concurrent_same_key_runs_factory_once()
    {
        var flight = new AsyncSingleFlight<string, int>();
        int callCount = 0;

        var tasks = new Task<int>[50];
        for (int k = 0; k < 50; k++)
        {
            tasks[k] = flight.ExecuteAsync("one", async _ =>
            {
                Interlocked.Increment(ref callCount);
                await Task.Delay(5).ConfigureAwait(false);
                return 42;
            }).AsTask();
        }

        int[] results = await Task.WhenAll(tasks);
        results.Should().OnlyContain(x => x == 42);
        callCount.Should().Be(1);
        flight.InFlightCount.Should().Be(0);
    }

    [Fact]
    public async Task InFlightCount_tracks_leader_and_returns_to_zero()
    {
        var flight = new AsyncSingleFlight<string, int>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> blocked = flight.ExecuteAsync("k", async _ =>
        {
            entered.SetResult();
            await Task.Delay(200).ConfigureAwait(false);
            return 1;
        }).AsTask();

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        flight.InFlightCount.Should().Be(1, because: "leader registered in-flight work");

        await blocked;
        flight.InFlightCount.Should().Be(0);
    }

    [Fact]
    public async Task Failure_propagates_to_all_waiters_and_clears_in_flight()
    {
        var flight = new AsyncSingleFlight<string, int>();
        var error = new InvalidOperationException("boom");

        Task<int>[] tasks = Enumerable.Range(0, 20)
            .Select(_ => flight.ExecuteAsync("bad", _ => new ValueTask<int>(Task.FromException<int>(error))).AsTask())
            .ToArray();

        foreach (Task<int> t in tasks)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await t);
        }

        flight.InFlightCount.Should().Be(0);
    }

    [Fact]
    public async Task Distinct_keys_run_in_parallel()
    {
        var flight = new AsyncSingleFlight<string, int>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int slowCalls = 0;

        Task<int> a = flight.ExecuteAsync("a", async _ =>
        {
            Interlocked.Increment(ref slowCalls);
            await gate.Task.ConfigureAwait(false);
            return 1;
        }).AsTask();

        await Task.Delay(50);
        slowCalls.Should().Be(1);

        Task<int> b = flight.ExecuteAsync("b", async _ => 2).AsTask();
        (await b).Should().Be(2);

        gate.SetResult();
        (await a).Should().Be(1);
        slowCalls.Should().Be(1);
        flight.InFlightCount.Should().Be(0);
    }

    [Fact]
    public void Empty_flight_has_zero_in_flight()
    {
        new AsyncSingleFlight<int, string>().InFlightCount.Should().Be(0);
    }
}
