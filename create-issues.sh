#!/usr/bin/env bash
# Usage: GH_TOKEN=<your-pat> REPO=thesarfo/distcache-dotnet bash create-issues.sh
# Requires: curl, jq

set -euo pipefail

REPO="${REPO:-thesarfo/distcache-dotnet}"
API="https://api.github.com"
AUTH="Authorization: Bearer ${GH_TOKEN}"

create_label() {
  local name="$1" color="$2" desc="$3"
  curl -sf -X POST "$API/repos/$REPO/labels" \
    -H "$AUTH" -H "Content-Type: application/json" \
    -d "{\"name\":\"$name\",\"color\":\"$color\",\"description\":\"$desc\"}" > /dev/null && echo "  label: $name" || true
}

create_milestone() {
  local title="$1" desc="$2"
  curl -sf -X POST "$API/repos/$REPO/milestones" \
    -H "$AUTH" -H "Content-Type: application/json" \
    -d "{\"title\":\"$title\",\"description\":\"$desc\",\"state\":\"open\"}" | jq -r '.number'
}

create_issue() {
  local title="$1" body="$2" labels="$3" milestone="$4"
  curl -sf -X POST "$API/repos/$REPO/issues" \
    -H "$AUTH" -H "Content-Type: application/json" \
    -d "{\"title\":$(echo "$title" | jq -Rs .),\"body\":$(echo "$body" | jq -Rs .),\"labels\":$labels,\"milestone\":$milestone}" > /dev/null \
    && echo "  issue: $title"
}

echo "==> Creating labels..."
create_label "sprint-1" "0075ca" "Sprint 1 — Foundation"
create_label "sprint-2" "e4e669" "Sprint 2 — Transport"
create_label "sprint-3" "d93f0b" "Sprint 3 — Discovery"
create_label "sprint-4" "0e8a16" "Sprint 4 — Resilience"
create_label "sprint-5" "5319e7" "Sprint 5 — Observability"
create_label "sprint-6" "b60205" "Sprint 6 — Production"
create_label "core" "bfd4f2" "Core engine work"
create_label "infra" "c5def5" "CI, packaging, tooling"
create_label "testing" "f9d0c4" "Tests and benchmarks"
create_label "transport" "fef2c0" "gRPC / networking"
create_label "discovery" "d4c5f9" "Cluster discovery providers"
create_label "resilience" "0052cc" "Circuit breakers, rate limiting"
create_label "observability" "e99695" "OTel, metrics, tracing"
create_label "docs" "cfd3d7" "Documentation"

echo "==> Creating milestones..."
M1=$(create_milestone "Sprint 1 — Foundation" "Weeks 1–2: project setup, interfaces, LRU, standalone engine")
M2=$(create_milestone "Sprint 2 — Transport" "Weeks 3–4: gRPC, consistent hashing, singleflight, peer manager")
M3=$(create_milestone "Sprint 3 — Discovery" "Weeks 5–7: discovery providers + warmup")
M4=$(create_milestone "Sprint 4 — Resilience" "Weeks 8–9: circuit breakers, rate limiting, hot keys")
M5=$(create_milestone "Sprint 5 — Observability" "Weeks 10–11: OTel metrics/tracing, admin endpoints, DI")
M6=$(create_milestone "Sprint 6 — Production" "Week 12: benchmarks, chaos testing, Helm chart, release")
echo "  milestones: $M1 $M2 $M3 $M4 $M5 $M6"

echo ""
echo "==> Sprint 1 issues..."

create_issue \
  "[S1] Create .NET 8 solution structure" \
  "## Goal
Create the multi-project .NET 8 solution with the correct project layout.

## Tasks
- Create solution file \`DistCache.sln\`
- Add projects: \`DistCache.Core\`, \`DistCache.Discovery\`, \`DistCache.Admin\`, \`DistCache.Tests\`, \`DistCache.Benchmarks\`
- Set \`<Nullable>enable</Nullable>\` and \`<ImplicitUsings>enable</ImplicitUsings>\` globally via \`Directory.Build.props\`
- Add \`.editorconfig\` with C# formatting rules
- Add Roslynator and StyleCop.Analyzers as dev dependencies

## Acceptance Criteria
- \`dotnet build\` passes on all projects
- Analyzers emit warnings on style violations" \
  '["sprint-1","infra"]' "$M1"

create_issue \
  "[S1] Set up GitHub Actions CI pipeline" \
  "## Goal
Automate build, test, and coverage reporting on every push/PR.

## Tasks
- Create \`.github/workflows/ci.yml\`: build + test on ubuntu-latest with .NET 8
- Add \`coverlet\` for coverage collection; fail if below 70%
- Upload coverage report as artifact and to Codecov or SonarQube
- Add a status badge to README

## Acceptance Criteria
- CI passes on a clean push
- PR checks block merge if tests fail" \
  '["sprint-1","infra"]' "$M1"

create_issue \
  "[S1] Set up NuGet packaging and semver tagging workflow" \
  "## Goal
Enable versioned NuGet package publishing from CI.

## Tasks
- Add \`Directory.Build.props\` with \`<PackageId>\`, \`<Authors>\`, \`<Description>\`, \`<RepositoryUrl>\`
- Create \`.github/workflows/publish.yml\`: triggers on \`v*\` tag push, packs and pushes to NuGet.org
- Configure \`NUGET_API_KEY\` secret in repository settings
- Set initial version to \`0.1.0-alpha\`

## Acceptance Criteria
- Tagging \`v0.1.0-alpha\` triggers publish workflow
- Package appears on NuGet feed with correct metadata" \
  '["sprint-1","infra"]' "$M1"

create_issue \
  "[S1] Define core public interfaces and value types" \
  "## Goal
Establish all public contracts that the rest of the engine will implement.

## Tasks
- Define \`IDataSource\` — \`FetchAsync(string key, CancellationToken ct)\`
- Define \`IKeySpace\` — Name, MaxBytes, Ttl, DataSource, WarmKeys
- Define \`IDistCacheEngine\` — Get/GetMany/Put/PutMany/Delete/DeleteMany/DeleteKeySpace/UpdateKeySpace/KeySpaces
- Define \`IDiscoveryProvider\` — StartAsync, GetPeersAsync, PeerChanges (IObservable\<PeerEvent\>)
- Define value types: \`CacheResult\`, \`CacheStats\`, \`PeerEvent\` (Join/Leave)
- Define config records: \`EngineConfig\`, \`KeySpaceOptions\`, \`StandaloneConfig\`

## Acceptance Criteria
- All interfaces in \`DistCache.Core\` namespace, documented with XML doc comments
- No circular dependencies between types" \
  '["sprint-1","core"]' "$M1"

create_issue \
  "[S1] Implement thread-safe LRU cache with byte-capacity tracking" \
  "## Goal
Build the local in-memory store that backs every cache node.

## Tasks
- Implement \`LruCache<TKey, TValue>\` using \`LinkedList\` + \`ConcurrentDictionary\`
- Track total byte size; evict LRU entry when \`MaxBytes\` exceeded
- Implement \`TryGet\`, \`Put\`, \`Remove\`, \`Clear\` methods
- Make all operations thread-safe (lock on linked-list mutations)

## Acceptance Criteria
- Unit tests: hit, miss, eviction on overflow, concurrent read/write
- No data races under \`Parallel.For\` stress test" \
  '["sprint-1","core"]' "$M1"

create_issue \
  "[S1] Implement TTL expiry with background sweep" \
  "## Goal
Automatically expire entries after their configured TTL without blocking reads.

## Tasks
- Store expiry timestamp alongside each cache entry
- Run a \`PeriodicTimer\` background task to sweep and remove expired entries
- On \`TryGet\`: if entry is past expiry, treat as miss and remove inline
- Make sweep interval configurable via \`KeySpaceOptions.SweepInterval\`

## Acceptance Criteria
- Expired entries are not returned after TTL elapses
- Background sweep does not block Get operations
- Integration test: set TTL=100ms → sleep 150ms → assert miss" \
  '["sprint-1","core"]' "$M1"

create_issue \
  "[S1] Implement KeySpace registry with add/remove/update" \
  "## Goal
Manage multiple named cache regions within a single engine instance.

## Tasks
- Implement \`KeySpaceRegistry\` backed by \`ConcurrentDictionary<string, KeySpaceEntry>\`
- Each entry holds the \`IKeySpace\` definition + its own \`LruCache\` instance
- Implement \`Register\`, \`Unregister\`, \`TryGet\`, \`Update\` operations
- Validate that keyspace names are non-empty and unique on register

## Acceptance Criteria
- Can register multiple keyspaces and route operations to the correct LRU
- \`Update\` swaps the definition atomically without losing in-flight reads" \
  '["sprint-1","core"]' "$M1"

create_issue \
  "[S1] Implement StandaloneEngine with DataSource fetch on miss" \
  "## Goal
Deliver a working single-node cache engine with read-through behaviour.

## Tasks
- Implement \`StandaloneEngine : IDistCacheEngine\`
- \`GetAsync\`: check LRU → on miss call \`IDataSource.FetchAsync\` → populate LRU → return
- Implement \`PutAsync\`, \`DeleteAsync\` and their batch variants (\`GetMany\`, \`PutMany\`, \`DeleteMany\`)
- Implement \`DeleteKeySpaceAsync\` and \`UpdateKeySpaceAsync\`
- Wire \`IDisposable\` / \`IAsyncDisposable\` to stop background TTL sweep

## Acceptance Criteria
- xUnit integration tests covering: hit, miss→fetch, expiry, eviction, batch ops
- Publish \`DistCache.Core 0.1.0-alpha\` to NuGet feed via CI" \
  '["sprint-1","core"]' "$M1"

echo ""
echo "==> Sprint 2 issues..."

create_issue \
  "[S2] Define cache.proto and generate C# gRPC stubs" \
  "## Goal
Establish the binary protocol all cache nodes speak to each other.

## Tasks
- Define \`cache.proto\` in \`DistCache.Core/Proto/\` with messages: \`GetRequest\`, \`GetResponse\`, \`PutRequest\`, \`DeleteRequest\`, \`BatchGetRequest\`, \`BatchGetResponse\`, \`BatchPutRequest\`
- Add \`CacheService\` service definition with corresponding RPCs
- Configure \`Grpc.Tools\` in \`DistCache.Core.csproj\` for code generation
- Commit generated stubs to \`obj/\` or use build-time generation

## Acceptance Criteria
- \`dotnet build\` generates C# stubs without errors
- Proto file is self-contained with no external imports beyond \`google/protobuf\`" \
  '["sprint-2","transport"]' "$M2"

create_issue \
  "[S2] Implement gRPC peer server and client" \
  "## Goal
Enable nodes to serve and call each other over HTTP/2 gRPC.

## Tasks
- Implement \`CachePeerServer\` (extends generated \`CacheService.CacheServiceBase\`): dispatch incoming RPCs to local \`LruCache\`
- Implement \`ICachePeerClient\` + \`GrpcCachePeerClient\`: wraps \`Grpc.Net.Client.GrpcChannel\` for outbound calls
- Register \`CachePeerServer\` via \`app.MapGrpcService<CachePeerServer>()\`
- Handle \`RpcException\` on client side; propagate as \`CacheException\`

## Acceptance Criteria
- Two in-process test nodes can exchange Get/Put/Delete via gRPC
- Errors from server side surface correctly on the client" \
  '["sprint-2","transport"]' "$M2"

create_issue \
  "[S2] Configure mTLS for peer-to-peer gRPC" \
  "## Goal
Secure all inter-node communication with mutual TLS.

## Tasks
- Load X.509 server certificate from file path or \`IConfiguration\` (PEM/PFX)
- Configure Kestrel to require client certificate (\`ClientCertificateMode.RequireCertificate\`)
- Validate client cert against a trusted CA on the server side
- Configure \`GrpcChannel\` on client side with \`HttpClientHandler\` presenting its own cert
- Provide a dev-mode flag to skip cert validation for local testing

## Acceptance Criteria
- mTLS handshake succeeds between two nodes with test certs
- Connection is rejected if client cert is missing or from wrong CA" \
  '["sprint-2","transport"]' "$M2"

create_issue \
  "[S2] Implement consistent hash ring with virtual nodes" \
  "## Goal
Deterministically map cache keys to peer nodes, with smooth rebalancing as nodes join/leave.

## Tasks
- Implement \`ConsistentHashRing\` using a sorted \`SortedDictionary<uint, string>\` of virtual nodes
- Hash peer addresses with \`FNV-1a\` or \`MurmurHash3\`; configurable replica count (default 150)
- \`GetOwner(key)\`: find the first virtual node ≥ hash(key), wrap around
- \`AddPeer\` / \`RemovePeer\`: update ring and return ownership diffs (keys that moved)

## Acceptance Criteria
- Determinism test: same key always maps to same peer for a fixed ring state
- Distribution test: keys spread within ±15% of uniform across nodes
- Ownership diff correctly identifies migrated keys when a peer joins/leaves" \
  '["sprint-2","transport"]' "$M2"

create_issue \
  "[S2] Implement local-vs-remote routing in the distributed engine" \
  "## Goal
Route each cache operation to the owning node — locally if self, via gRPC if remote.

## Tasks
- Implement \`DistributedEngine : IDistCacheEngine\` wrapping \`ConsistentHashRing\` + \`PeerManager\`
- On \`GetAsync\`: hash key → if owner == self → local LRU, else → \`ICachePeerClient.GetAsync\`
- Same pattern for Put, Delete
- On remote peer failure, fall back to calling \`IDataSource.FetchAsync\` locally

## Acceptance Criteria
- Integration test: 2-node setup — keys owned by node B are fetched correctly from node A's perspective
- Fallback is triggered when peer gRPC call returns \`Unavailable\`" \
  '["sprint-2","transport"]' "$M2"

create_issue \
  "[S2] Implement AsyncSingleFlight<T> for request coalescing" \
  "## Goal
Prevent thundering-herd cache misses by coalescing concurrent requests for the same key.

## Tasks
- Implement \`AsyncSingleFlight<TKey, TResult>\` using \`ConcurrentDictionary<TKey, Task<TResult>>\`
- \`ExecuteAsync(key, factory)\`: if a request for \`key\` is in-flight, return the shared \`Task\`; else start factory and register it
- Remove key from dictionary on completion (success or failure)
- Track in-flight count in \`CacheStats.InFlightRequests\`

## Acceptance Criteria
- Stress test: 50 concurrent \`GetAsync\` for the same key triggers \`FetchAsync\` exactly once
- In-flight counter increments/decrements correctly" \
  '["sprint-2","core"]' "$M2"

create_issue \
  "[S2] Implement PeerManager with gRPC connection pooling" \
  "## Goal
Maintain the live peer list and reusable gRPC channels.

## Tasks
- Implement \`PeerManager\`: \`ConcurrentDictionary<string, GrpcChannel>\` keyed by peer address
- Subscribe to \`IDiscoveryProvider.PeerChanges()\`; on Join → add channel, on Leave → shut down channel
- Configure channel options: keepalive ping interval, max idle timeout
- Expose \`PeerManager.Peers\` (\`IReadOnlyList<string>\`) for admin endpoint
- Integrate \`StaticDiscoveryProvider\` for fixed peer list (used in tests + demos)

## Acceptance Criteria
- Peer joins trigger channel creation; leaves trigger graceful channel shutdown
- Connection pool does not create duplicate channels for the same address" \
  '["sprint-2","transport"]' "$M2"

echo ""
echo "==> Sprint 3 issues..."

create_issue \
  "[S3] Implement Kubernetes discovery provider" \
  "## Goal
Automatically detect cache peers from Kubernetes Endpoints without manual config.

## Tasks
- Use \`KubernetesClient\` to watch Endpoints for a headless Service (configurable label selector)
- Translate \`Added\`/\`Modified\`/\`Deleted\` watch events into \`PeerEvent.Join\`/\`PeerEvent.Leave\`
- Support both in-cluster \`ServiceAccount\` auth and out-of-cluster \`kubeconfig\`
- Expose \`KubernetesDiscoveryOptions\`: namespace, labelSelector, port name
- Write integration tests using \`k3d\` or \`kind\` in CI (\`docker\` service in GitHub Actions)

## Acceptance Criteria
- Adding/removing a pod from the headless Service triggers the correct \`PeerEvent\`
- Provider handles watch reconnection on API server timeout" \
  '["sprint-3","discovery"]' "$M3"

create_issue \
  "[S3] Implement NATS discovery provider" \
  "## Goal
Use NATS pub/sub for lightweight peer heartbeating and topology awareness.

## Tasks
- Use \`NATS.Net\` client; each node publishes a heartbeat on \`distcache.peers\` every N seconds
- Maintain a peer table; expire entries after \`3 × heartbeat interval\` (missed heartbeats)
- Emit \`PeerEvent.Join\` on first heartbeat from a new peer, \`Leave\` on expiry
- Support NATS JetStream for durable peer state (optional, via config flag)
- Integration tests with NATS Docker Compose fixture in CI

## Acceptance Criteria
- Peer table converges correctly across three in-process nodes
- Stopping a node's heartbeat causes it to be evicted from the table after the TTL window" \
  '["sprint-3","discovery"]' "$M3"

create_issue \
  "[S3] Implement DNS SRV, Static, and Standalone discovery providers" \
  "## Goal
Cover the remaining three discovery modes and complete the \`IDiscoveryProvider\` suite.

## Tasks
- **DNS**: poll configured SRV record on a configurable interval; diff results to emit \`PeerEvent\`s
- **Static**: read immutable peer list from \`StaticDiscoveryOptions.Peers\`; emit \`Join\` at startup
- **Standalone**: no-op implementation — single node, zero network calls, no peer events
- All three must implement the shared \`IDiscoveryProvider\` contract
- Create a shared \`DiscoveryProviderTestBase\` xUnit base class exercising the common contract

## Acceptance Criteria
- Each provider passes the shared test base
- Standalone provider passes without any network access" \
  '["sprint-3","discovery"]' "$M3"

create_issue \
  "[S3] Implement hot-key warmup on topology change" \
  "## Goal
Pre-populate keys on a newly responsible node so the first real requests hit cache, not the DataSource.

## Tasks
- On peer Join/Leave: recompute ring ownership diffs using \`ConsistentHashRing.AddPeer\`/\`RemovePeer\`
- For keys now owned by this node: call \`IKeySpace.WarmKeys\` + fetch from \`IDataSource\` and populate LRU
- Configurable per-keyspace: \`WarmupConcurrency\` (max parallel fetches), \`WarmupTimeout\`
- Emit \`cache.warmup.keys_fetched\` metric counter
- Integration test: add a second node → verify configured warm keys are in its LRU before any client request

## Acceptance Criteria
- Warm keys appear in LRU within \`WarmupTimeout\` of a topology change
- Warmup does not block the engine from serving live requests" \
  '["sprint-3","core"]' "$M3"

echo ""
echo "==> Sprint 4 issues..."

create_issue \
  "[S4] Implement CircuitBreaker<T> for DataSource protection" \
  "## Goal
Stop cascading failures when a DataSource is unavailable or slow.

## Tasks
- Implement \`CircuitBreaker<T>\` state machine: Closed → Open (on failure threshold) → Half-Open (after open duration) → Closed
- Configurable: \`FailureThreshold\`, \`SamplingWindow\`, \`OpenDuration\`
- Wrap \`IDataSource.FetchAsync\` with circuit breaker in the engine
- On Open state: throw \`CacheException { Reason = CircuitOpen }\` immediately (no fetch)
- Emit \`cache.circuit_breaker.state\` gauge metric on state transitions

## Acceptance Criteria
- Circuit opens after N consecutive failures within the sampling window
- Half-open probe succeeds → circuit closes; probe fails → stays open
- Unit tests cover all three state transitions" \
  '["sprint-4","resilience"]' "$M4"

create_issue \
  "[S4] Implement token-bucket rate limiter per keyspace" \
  "## Goal
Prevent a burst of cache misses from overwhelming the DataSource.

## Tasks
- Use \`System.Threading.RateLimiting.TokenBucketRateLimiter\` per keyspace
- Configure via \`KeySpaceOptions.RateLimit\`: \`TokensPerSecond\`, \`BucketSize\`
- Global default override via \`EngineConfig.DefaultRateLimit\`
- On limit exceeded: throw \`CacheException { Reason = RateLimited }\`
- Load test with NBomber: verify limiter enforces configured RPS ceiling

## Acceptance Criteria
- Requests beyond the rate limit are rejected immediately with \`RateLimited\` status
- Under-limit traffic flows through without any added latency" \
  '["sprint-4","resilience"]' "$M4"

create_issue \
  "[S4] Implement Count-Min Sketch hot key tracker" \
  "## Goal
Track which keys are accessed most frequently for intelligent warmup and observability.

## Tasks
- Implement \`CountMinSketch\` with configurable width/depth; use pairwise-independent hash functions
- Increment sketch counter on every \`GetAsync\` call per keyspace
- Implement \`TopN(n)\`: return top-N keys using a min-heap over sketch estimates
- Apply configurable decay (halve all counters every \`DecayInterval\`) to prefer recent hotness
- Expose via \`IDistCacheEngine.HotKeys(keySpace, n)\` and feed into warmup logic

## Acceptance Criteria
- Sketch correctly identifies the top-5 most-accessed keys in a skewed workload
- Decay causes stale hot keys to fall out of the top-N after the decay window" \
  '["sprint-4","core"]' "$M4"

create_issue \
  "[S4] Implement UpdateKeySpaceAsync for live config changes" \
  "## Goal
Allow operators to change TTL, byte limits, and rate limits at runtime without restarting the engine.

## Tasks
- \`UpdateKeySpaceAsync\`: validate the new \`IKeySpace\` definition (non-null DataSource, positive MaxBytes)
- Atomically swap the keyspace entry in \`KeySpaceRegistry\` using \`Interlocked\` or a write lock
- Resize the underlying \`LruCache\` if \`MaxBytes\` changed (evict excess entries if shrinking)
- Re-configure the rate limiter and circuit breaker with new \`KeySpaceOptions\`
- Integration test: change TTL from 10 min to 1 s → verify entries expire with new TTL

## Acceptance Criteria
- No requests fail or return stale config during the swap
- Concurrent \`GetAsync\` + \`UpdateKeySpaceAsync\` stress test passes without data races" \
  '["sprint-4","core"]' "$M4"

echo ""
echo "==> Sprint 5 issues..."

create_issue \
  "[S5] Instrument cache operations with OpenTelemetry metrics" \
  "## Goal
Export standard cache metrics to any OTLP-compatible backend (Prometheus, Datadog, etc.)

## Tasks
- Register \`Meter(\"DistCache\", version)\` via \`OpenTelemetry.Extensions.Hosting\`
- **Counters**: \`cache.hits\`, \`cache.misses\`, \`cache.evictions\`, \`cache.puts\`, \`cache.deletes\` — tagged with \`keyspace\`
- **Histograms**: \`cache.get.duration\`, \`cache.fetch.duration\`, \`cache.put.duration\` (milliseconds)
- **Gauges**: \`cache.keyspace.bytes\`, \`cache.keyspace.entries\`, \`cache.peers.active\`
- Integration test: Prometheus scrape endpoint returns all expected metric names

## Acceptance Criteria
- All metrics appear in Prometheus with correct labels after a round of Get/Put operations" \
  '["sprint-5","observability"]' "$M5"

create_issue \
  "[S5] Add OpenTelemetry distributed tracing" \
  "## Goal
Provide end-to-end traces from the caller through cache routing to DataSource fetch.

## Tasks
- Register \`ActivitySource(\"DistCache\")\`
- Create child spans for: \`GetAsync\`, \`PutAsync\`, \`DeleteAsync\`, \`FetchAsync\`, \`Warmup\`
- Tag spans: \`cache.keyspace\`, \`cache.hit\` (bool), \`peer.address\`, \`datasource.name\`
- Propagate W3C \`traceparent\` header over gRPC using \`Grpc.Core.Interceptors\`
- Export traces to OTLP collector in Docker Compose integration test stack; verify with Jaeger UI

## Acceptance Criteria
- A cache miss produces a trace with parent span (Get) → child span (Fetch) visible in Jaeger
- \`cache.hit=true\` spans do not include a Fetch child span" \
  '["sprint-5","observability"]' "$M5"

create_issue \
  "[S5] Implement JSON admin HTTP endpoints" \
  "## Goal
Expose live engine diagnostics without requiring external tooling.

## Tasks
- \`GET /admin/peers\` → JSON array of \`{ address, status, latencyMs }\`
- \`GET /admin/keyspaces\` → JSON array of per-keyspace stats (hits, misses, bytes, entries, hitRatio)
- \`GET /admin/keyspaces/{name}/hotkeys?n=10\` → top-N hot keys for that keyspace
- Secure endpoints: configurable IP allowlist (\`AdminOptions.AllowedCidrs\`) or bearer token
- Mount admin routes on a separate port (default 9099) to isolate from application traffic

## Acceptance Criteria
- Endpoints return correct data after a round of cache operations
- Requests from unauthorized IPs return \`403\`" \
  '["sprint-5","observability"]' "$M5"

create_issue \
  "[S5] Add DI extensions, health checks, and IHostedService integration" \
  "## Goal
Make DistCache.NET a first-class ASP.NET Core citizen via Microsoft.Extensions.

## Tasks
- \`AddDistCache(Action<EngineConfig>)\` extension on \`IServiceCollection\`: registers \`IDistCacheEngine\` as singleton, starts engine via \`IHostedService\`
- \`AddDistCacheOtel()\` extension on \`IOpenTelemetryBuilder\`: registers \`Meter\` and \`ActivitySource\`
- Implement \`IHealthCheck\`: \`Healthy\` if engine is started and peer count ≥ 1 (or standalone); \`Degraded\` if peer count < expected
- Write integration test: \`WebApplicationFactory\` + health endpoint returns \`Healthy\`

## Acceptance Criteria
- A blank ASP.NET Core app can add DistCache with 3 lines of code
- Health check is visible at \`/healthz\` with correct status" \
  '["sprint-5","core","observability"]' "$M5"

echo ""
echo "==> Sprint 6 issues..."

create_issue \
  "[S6] Run BenchmarkDotNet performance baselines" \
  "## Goal
Establish throughput and latency baselines that gate future regressions.

## Tasks
- Single-node benchmarks: \`GetAsync\` (hot path), \`PutAsync\`, \`DeleteAsync\` — report ops/sec and p50/p99
- Distributed benchmark: 3-node cluster, 100k keys, 80/20 read/write ratio
- Use \`MemoryDiagnoser\` to measure allocations per operation; eliminate hot-path boxing
- Publish results to GitHub Pages via CI artifact upload on every release tag

## Acceptance Criteria
- Single-node Get p99 < 1 ms (in-process)
- Zero allocations on the hot Get path (cache hit, no fetch) — verified by MemoryDiagnoser" \
  '["sprint-6","testing"]' "$M6"

create_issue \
  "[S6] Chaos and edge-case hardening" \
  "## Goal
Prove the engine handles failure scenarios gracefully without data loss or deadlock.

## Tasks
- Simulate peer crash mid-request: gRPC call throws \`RpcException(Unavailable)\` → verify fallback to local DataSource fetch
- Test \`DataSource\` returning \`null\`: verify negative result is cached (no repeated fetch storms)
- Concurrent \`UpdateKeySpaceAsync\` + \`GetAsync\` stress test: 10 threads for 5 seconds, assert no exceptions or corrupted state
- Graceful shutdown: call \`DisposeAsync\` while 100 in-flight requests are pending; verify all complete or are cancelled cleanly

## Acceptance Criteria
- All four scenarios pass without deadlock, data corruption, or unhandled exceptions" \
  '["sprint-6","testing","resilience"]' "$M6"

create_issue \
  "[S6] Create Kubernetes Helm chart" \
  "## Goal
Provide a production-ready Helm chart for deploying DistCache.NET on Kubernetes.

## Tasks
- \`StatefulSet\` with \`podAntiAffinity\` (spread across nodes) and \`PodDisruptionBudget\` (min available = N-1)
- \`Headless Service\` for peer-to-peer gRPC discovery (used by \`KubernetesDiscoveryProvider\`)
- \`ConfigMap\` for engine config JSON; \`Secret\` for mTLS certificates
- \`HorizontalPodAutoscaler\` targeting \`cache.get.duration p99 < 5ms\` via custom metrics adapter
- Validate chart with \`helm lint\` and \`helm template\` in CI

## Acceptance Criteria
- \`helm install distcache ./chart\` on a fresh \`k3d\` cluster brings up a 3-node cache ring
- HPA scales up when p99 exceeds threshold in a load test" \
  '["sprint-6","infra"]' "$M6"

create_issue \
  "[S6] Write documentation, example app, and cut 1.0.0 release" \
  "## Goal
Ship a polished, well-documented 1.0.0 that developers can adopt with confidence.

## Tasks
- Comprehensive README: Quick Start, configuration reference, discovery provider guide
- \`docs/\` directory: architecture overview, API reference (generated from XML docs), performance guide
- \`examples/\` directory: ASP.NET Core app using DistCache with NATS discovery and a mock DataSource
- Ensure CI coverage gate ≥ 80%; generate and commit coverage badge to README
- Tag \`v1.0.0\`, trigger publish workflow → \`DistCache.Core\`, \`DistCache.Discovery\`, \`DistCache.Admin\` on NuGet.org

## Acceptance Criteria
- Example app builds and runs end-to-end with \`docker compose up\`
- NuGet packages appear publicly on nuget.org with correct version and metadata" \
  '["sprint-6","docs"]' "$M6"

echo ""
echo "==> Done! All issues created at https://github.com/$REPO/issues"
