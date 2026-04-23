using BenchmarkDotNet.Attributes;
using DistCache.Core.Abstractions;
using DistCache.Core.Configuration;
using DistCache.Core.Engine;

namespace DistCache.Benchmarks;

/// <summary>
/// Micro-benchmarks for <see cref="StandaloneEngine"/> (cache hit after read-through warm-up).
/// </summary>
public class EngineBenchmarks
{
    private const string KeySpaceName = "ks";
    private const string HotKey = "hot";

    private StandaloneEngine? engine;

    [GlobalSetup]
    public void Setup()
    {
        IDataSource ds = new ConstantDataSource(new byte[] { 1, 2, 3, 4 });
        engine = new StandaloneEngine(
            new EngineConfig(
                null,
                new[]
                {
                    new KeySpaceOptions(KeySpaceName, 1_000_000, null, ds, Array.Empty<string>()),
                }));

        _ = engine.GetAsync(KeySpaceName, HotKey, default).AsTask().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (engine is not null)
            await engine.DisposeAsync();
    }

    [Benchmark]
    public Task GetAsync_CacheHit() => engine!.GetAsync(KeySpaceName, HotKey, default).AsTask();

    private sealed class ConstantDataSource : IDataSource
    {
        private readonly byte[] payload;

        public ConstantDataSource(byte[] payload) => this.payload = payload;

        public ValueTask<ReadOnlyMemory<byte>?> FetchAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>?>(payload);
    }
}
