# DistCache.NET

A distributed read-through cache engine 

<!-- in C#, ported from the Go reference implementation. -->

## Overview

DistCache.NET is a high-performance, distributed caching library for .NET 8 that provides:

- **Read-Through Cache**, automatic fetch from your `IDataSource` on miss, no miss handling in application code
- **Consistent Hashing**, data partitioned across nodes with virtual nodes; automatic rebalancing on topology change
- **KeySpace Model**, namespaced cache regions with per-keyspace TTL, byte limits, and live config updates
- **Pluggable Discovery**, Kubernetes, NATS, DNS SRV, Static, and Standalone providers
- **Resilience**, circuit breakers, token-bucket rate limiting, and singleflight (request coalescing)
- **Observability**, OpenTelemetry metrics + traces, JSON admin endpoints

## License

MIT
