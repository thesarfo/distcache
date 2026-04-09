# DistCache.NET

A distributed read-through cache engine in C#, ported from the Go reference implementation.

## Overview

DistCache.NET is a high-performance, distributed caching library for .NET 8 that provides:

- **Read-Through Cache**, automatic fetch from your `IDataSource` on miss, no miss handling in application code
- **Consistent Hashing**, data partitioned across nodes with virtual nodes; automatic rebalancing on topology change
- **KeySpace Model**, namespaced cache regions with per-keyspace TTL, byte limits, and live config updates
- **Pluggable Discovery**, Kubernetes, NATS, DNS SRV, Static, and Standalone providers
- **Resilience**, circuit breakers, token-bucket rate limiting, and singleflight (request coalescing)
- **Observability**, OpenTelemetry metrics + traces, JSON admin endpoints

## Architecture

```
Your App
   │
   ▼ Engine.Get / Put / Delete
IDistCacheEngine
   │ KeySpace Registry · Request Router
   ▼ Consistent Hashing → Peer Selection
Cache Layer
   │ LRU Store · Singleflight · Hot-Key Tracker · Warmup
   ▼ gRPC / HTTP2 (mTLS) ↔ Peers
Transport
   │ gRPC Peer Client · gRPC Peer Server · Admin HTTP
   ▼ Cache Miss → Fetch from DataSource
Resilience
   │ Circuit Breaker · Rate Limiter · IDataSource.Fetch
   ↔ Discovery Events (Join/Leave)
Discovery
   │ Kubernetes · NATS · DNS · Static · Standalone
```

## Core Interfaces

```csharp
public interface IDataSource
{
    Task<byte[]?> FetchAsync(string key, CancellationToken ct = default);
}

public interface IDistCacheEngine : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct = default);
    Task<byte[]?> GetAsync(string keySpace, string key, CancellationToken ct = default);
    Task PutAsync(string keySpace, string key, byte[] value, TimeSpan? ttl = null, CancellationToken ct = default);
    Task DeleteAsync(string keySpace, string key, CancellationToken ct = default);
    Task UpdateKeySpaceAsync(IKeySpace newDefinition, CancellationToken ct = default);
    IReadOnlyList<string> KeySpaces();
}

public interface IDiscoveryProvider : IAsyncDisposable
{
    Task StartAsync(string selfAddress, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetPeersAsync(CancellationToken ct = default);
    IObservable<PeerEvent> PeerChanges();
}
```

## Sprint Plan

| Sprint | Focus | Weeks |
|--------|-------|-------|
| 1 | Project Setup & Core Abstractions | 1–2 |
| 2 | gRPC Transport & Consistent Hashing | 3–4 |
| 3 | Discovery Providers | 5–7 |
| 4 | Circuit Breakers, Rate Limiting & Hot Keys | 8–9 |
| 5 | OpenTelemetry & Admin Endpoints | 10–11 |
| 6 | Hardening, Benchmarks & Release | 12 |

## Tech Stack

- **Runtime**: .NET 8 / C# 12
- **Transport**: gRPC (Grpc.AspNetCore, Grpc.Net.Client)
- **Serialization**: Protobuf
- **Observability**: OpenTelemetry (metrics + tracing, OTLP export)
- **Discovery**: KubernetesClient, NATS.Net
- **Rate Limiting**: System.Threading.RateLimiting
- **Testing**: xUnit, NSubstitute, FluentAssertions, NBomber
- **Benchmarks**: BenchmarkDotNet

## Quick Start

```csharp
builder.Services.AddDistCache(options =>
{
    options.SelfAddress = "localhost:5001";
    options.Discovery = new StandaloneDiscoveryProvider();
})
.AddKeySpace(new KeySpaceOptions
{
    Name = "products",
    MaxBytes = 100 * 1024 * 1024, // 100 MB
    Ttl = TimeSpan.FromMinutes(15),
    DataSource = new ProductDataSource(db),
});

// In your code:
var engine = app.Services.GetRequiredService<IDistCacheEngine>();
var data = await engine.GetAsync("products", productId);
```

## License

MIT
