# Design: Bifrost Benchmark Suite (0.2.0)

## Problem Statement

Bifrost v0.1.0 makes specific performance claims — zero-allocation enqueue operations, sub-100ns latency, >1M items/sec worker throughput — but has no benchmarks to validate them. Without measured baselines, performance regressions will go undetected as features are added in 0.3.0–0.5.0.

The benchmark suite must validate the architectural claims from the original design document and establish baselines that future releases build on.

## Chosen Approach

**Focused Core Benchmarks** — implement ~8-10 benchmark classes targeting the hot paths and architectural claims that matter most. Skip trivial property-access benchmarks and timing-sensitive E2E scenarios (backpressure, graceful shutdown) that produce noisy results in BenchmarkDotNet.

### What's In Scope

| Category | Benchmarks | Why |
|----------|-----------|-----|
| **Enqueue latency** | `EnqueueAsync`, `TryEnqueue`, `Run`, `TryRun` | Core hot path — the most important perf surface |
| **Allocation validation** | Enqueue ops, worker loop steady-state, event structs | Zero-alloc is an architectural claim that must be proven |
| **Worker throughput** | Single-worker, multi-worker items/sec | Validates >1M items/sec target |
| **Decorator overhead** | Per-layer cost, stacked decorator cost | Quantifies the price of each decorator |
| **Autoscaling internals** | Scaling decisions, WorkerMetrics, WorkerRegistry | Hot path for autoscaling decorator |

### What's Out of Scope

| Category | Why Excluded |
|----------|-------------|
| Property access (`PendingCount`, `Capacity`, etc.) | Trivial reads — benchmarking them adds noise, not signal |
| Health check latency | Not a hot path — runs on a timer, ~once/sec |
| Backpressure behavior | Inherently timing-sensitive, better validated in integration tests |
| Graceful shutdown | Timing-dependent lifecycle, not a BenchmarkDotNet target |
| Throughput under sustained load | Better measured with a dedicated load-test harness |

---

## Technical Design

### Project Structure

```text
src/Bifrost.Benchmarks/
├── Bifrost.Benchmarks.csproj
├── Program.cs                              # BenchmarkSwitcher
├── Core/
│   ├── EnqueueBenchmarks.cs               # EnqueueAsync, TryEnqueue, Run, TryRun latency
│   └── WorkerThroughputBenchmarks.cs      # Items/sec: single & multi-worker
├── Allocation/
│   ├── EnqueueAllocationBenchmarks.cs     # Zero-alloc validation for enqueue ops
│   ├── WorkerLoopAllocationBenchmarks.cs  # Steady-state worker loop allocations
│   └── EventStreamAllocationBenchmarks.cs # Event struct allocation validation
├── Decorators/
│   ├── DecoratorOverheadBenchmarks.cs     # Per-layer latency cost
│   ├── AutoscalingOverheadBenchmarks.cs   # Metrics collection (Interlocked) cost
│   └── ResilienceOverheadBenchmarks.cs    # Polly pipeline overhead
└── Autoscaling/
    ├── ScalingDecisionBenchmarks.cs       # Watermark evaluation speed
    └── MetricsCollectionBenchmarks.cs     # WorkerMetrics Interlocked counter cost
```

**10 benchmark classes total.**

### Project File

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <NoWarn>$(NoWarn);CA1822</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Bifrost\Bifrost.csproj" />
    <ProjectReference Include="..\Bifrost.Resilience\Bifrost.Resilience.csproj" />
  </ItemGroup>
</Project>
```

Target framework, nullable, and implicit usings inherited from `Directory.Build.props`. BenchmarkDotNet version added to `Directory.Packages.props`.

### Program.cs

```csharp
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Bifrost.Benchmarks.Allocation;
using Bifrost.Benchmarks.Autoscaling;
using Bifrost.Benchmarks.Core;
using Bifrost.Benchmarks.Decorators;

var config = ManualConfig
    .Create(DefaultConfig.Instance)
    .WithOptions(ConfigOptions.DisableOptimizationsValidator);

var switcher = new BenchmarkSwitcher(
[
    // Core
    typeof(EnqueueBenchmarks),
    typeof(WorkerThroughputBenchmarks),

    // Allocation validation
    typeof(EnqueueAllocationBenchmarks),
    typeof(WorkerLoopAllocationBenchmarks),
    typeof(EventStreamAllocationBenchmarks),

    // Decorator overhead
    typeof(DecoratorOverheadBenchmarks),
    typeof(AutoscalingOverheadBenchmarks),
    typeof(ResilienceOverheadBenchmarks),

    // Autoscaling internals
    typeof(ScalingDecisionBenchmarks),
    typeof(MetricsCollectionBenchmarks),
]);

switcher.Run(args, config);
```

Run examples:
```bash
dotnet run -c Release -- --filter "*Enqueue*"
dotnet run -c Release -- --filter "*Allocation*"
dotnet run -c Release -- --list flat
dotnet run -c Release -- --job Dry   # CI smoke test
```

---

### Benchmark Designs

#### 1. EnqueueBenchmarks (Core/)

Measures latency of all four enqueue paths on a pre-warmed orchestrator.

```csharp
[MemoryDiagnoser]
public class EnqueueBenchmarks
{
    [Params(128, 1024)]
    public int Capacity { get; set; }

    // GlobalSetup: create orchestrator with no-op handler, drain workers
    // IterationSetup: ensure queue is near-empty (consume items)

    [Benchmark(Baseline = true)]
    public bool TryEnqueue();           // Target: < 50ns

    [Benchmark]
    public ValueTask EnqueueAsync();    // Target: < 100ns

    [Benchmark]
    public void Run();                  // Sync variant

    [Benchmark]
    public bool TryRun();               // Sync try variant
}
```

**Key setup concern:** Workers must be suspended or using a no-op handler that drains instantly. The benchmark measures enqueue cost, not processing. Use a `Channel<int>` with workers paused (create orchestrator, don't start hosted service). The orchestrator starts workers in `StartAsync` — for benchmarks, we measure the channel write directly.

**Alternative approach:** Create the orchestrator with a large capacity and only enqueue a small number of items per iteration, then reset in `IterationSetup`. This avoids the complexity of pausing workers.

#### 2. WorkerThroughputBenchmarks (Core/)

Measures end-to-end items/sec through the orchestrator.

```csharp
[MemoryDiagnoser]
public class WorkerThroughputBenchmarks
{
    [Params(1, 4, 16)]
    public int WorkerCount { get; set; }

    private const int ItemCount = 100_000;

    // GlobalSetup: create orchestrator with immediate-return handler
    // Benchmark: enqueue ItemCount items, wait for all to complete

    [Benchmark]
    public async Task Throughput();     // Target: > 1M items/sec (single worker)
}
```

**Completion tracking:** Use a `CountdownEvent` or `TaskCompletionSource` signaled by the handler after processing each item. The benchmark measures time from first enqueue to last completion.

#### 3. EnqueueAllocationBenchmarks (Allocation/)

Validates zero-allocation claims on the hot path.

```csharp
[MemoryDiagnoser]
public class EnqueueAllocationBenchmarks
{
    // GlobalSetup: create orchestrator, warmup with 100 enqueue/dequeue cycles

    [Benchmark]
    public bool TryEnqueue_ZeroAlloc();     // Expected: 0 B

    [Benchmark]
    public ValueTask EnqueueAsync_ZeroAlloc();  // Expected: 0 B (sync path)
}
```

**Critical detail:** `EnqueueAsync` calls `_channel.Writer.WriteAsync()` which returns a `ValueTask`. When the channel has capacity, this completes synchronously and allocates 0 bytes. The benchmark must ensure the channel has capacity (large buffer, items being consumed) so we measure the synchronous fast path.

#### 4. WorkerLoopAllocationBenchmarks (Allocation/)

Validates steady-state worker loop allocates nothing.

```csharp
[MemoryDiagnoser]
public class WorkerLoopAllocationBenchmarks
{
    // GlobalSetup: start orchestrator, warmup 1000 items to stabilize JIT
    // Benchmark: enqueue + process N items, measure allocations

    [Benchmark]
    public async Task WorkerLoop_SteadyState();  // Expected: 0 B per item
}
```

**Note:** The worker loop uses `await foreach` on `ChannelReader.ReadAllAsync()`. In steady state this should allocate nothing — the async state machine is already allocated and the `IAsyncEnumerator` is reused.

#### 5. EventStreamAllocationBenchmarks (Allocation/)

Validates event structs stay on the stack.

```csharp
[MemoryDiagnoser]
public class EventStreamAllocationBenchmarks
{
    // GlobalSetup: create event stream orchestrator with 1 subscriber

    [Benchmark]
    public bool TryEnqueue_WithEventStream();  // Validates event struct doesn't box
}
```

**Key concern:** `WorkEnqueuedEvent<TWork>` is a `readonly record struct` implementing `ICorrelatedEvent` (interface). When written to `Channel<IOrchestratorEvent>`, it boxes. This benchmark quantifies that cost. The event broadcast path uses `TryWrite` with an `IOrchestratorEvent` parameter — the struct will be boxed at the channel write boundary.

This is an important finding for the benchmark: the "zero-alloc event" claim may not hold when events are published to the subscriber channel. The benchmark will reveal whether this boxing occurs and how much it costs.

#### 6. DecoratorOverheadBenchmarks (Decorators/)

Measures per-layer cost by comparing bare orchestrator vs. decorated.

```csharp
[MemoryDiagnoser]
public class DecoratorOverheadBenchmarks
{
    // GlobalSetup: create bare orchestrator, autoscaling-wrapped, event-stream-wrapped, and fully-stacked

    [Benchmark(Baseline = true)]
    public bool TryEnqueue_Bare();              // Baseline

    [Benchmark]
    public bool TryEnqueue_WithAutoscaling();   // +Interlocked overhead

    [Benchmark]
    public bool TryEnqueue_WithEventStream();   // +Event broadcast overhead

    [Benchmark]
    public bool TryEnqueue_FullStack();         // All decorators
}
```

**Target:** < 20ns per decorator layer.

#### 7. AutoscalingOverheadBenchmarks (Decorators/)

Isolates the cost of the autoscaling decorator's metrics collection.

```csharp
[MemoryDiagnoser]
public class AutoscalingOverheadBenchmarks
{
    [Params(32, 128, 1024)]
    public int Capacity { get; set; }

    [Benchmark]
    public bool TryEnqueue_MetricsEnabled();

    [Benchmark]
    public bool TryEnqueue_MetricsDisabled();  // Autoscaling with metrics off
}
```

#### 8. ResilienceOverheadBenchmarks (Decorators/)

Measures Polly pipeline overhead on the happy path (no retries triggered).

```csharp
[MemoryDiagnoser]
public class ResilienceOverheadBenchmarks
{
    [Benchmark(Baseline = true)]
    public ValueTask EnqueueAsync_Bare();

    [Benchmark]
    public ValueTask EnqueueAsync_WithResilience();
}
```

**Note:** The resilience decorator wraps `EnqueueAsync` in `_policy.ExecuteAsync()` with a lambda and `new Context()`. This will allocate. The benchmark quantifies the cost so users can make informed decisions about enabling resilience.

#### 9. ScalingDecisionBenchmarks (Autoscaling/)

Measures how fast the autoscaling engine evaluates watermarks.

```csharp
[MemoryDiagnoser]
public class ScalingDecisionBenchmarks
{
    [Params(0.1, 0.5, 0.9)]
    public double Utilization { get; set; }

    [Benchmark]
    public Task EvaluateScaling();  // Single watermark evaluation
}
```

#### 10. MetricsCollectionBenchmarks (Autoscaling/)

Measures the cost of `WorkerMetrics` Interlocked operations.

```csharp
[MemoryDiagnoser]
public class MetricsCollectionBenchmarks
{
    [Benchmark]
    public void RecordEnqueue();           // Interlocked.Increment

    [Benchmark]
    public void RecordDequeue();           // Interlocked.Decrement

    [Benchmark]
    public double CalculateUtilization();  // Interlocked.Read + division
}
```

---

### Target Metrics

| Metric | Target | Benchmark Class |
|--------|--------|----------------|
| `EnqueueAsync` latency | < 100ns | `EnqueueBenchmarks` |
| `TryEnqueue` latency | < 50ns | `EnqueueBenchmarks` |
| `EnqueueAsync` allocations | 0 B | `EnqueueAllocationBenchmarks` |
| `TryEnqueue` allocations | 0 B | `EnqueueAllocationBenchmarks` |
| Worker throughput (single) | > 1M items/sec | `WorkerThroughputBenchmarks` |
| Event struct allocations | Quantified | `EventStreamAllocationBenchmarks` |
| Decorator layer overhead | < 20ns per layer | `DecoratorOverheadBenchmarks` |
| WorkerMetrics overhead | < 10ns | `MetricsCollectionBenchmarks` |

---

## Integration Points

### Solution Integration

Add `Bifrost.Benchmarks` to `src/Bifrost.sln`. The project references `Bifrost` and `Bifrost.Resilience` (for resilience overhead benchmarks). It does not reference `Bifrost.HealthChecks` or `Bifrost.OpenTelemetry` since those benchmarks are excluded.

### CI Integration

Add a benchmark smoke-test step to the CI workflow:

```yaml
- name: Benchmark smoke test
  run: >-
    dotnet run
    --project src/Bifrost.Benchmarks/Bifrost.Benchmarks.csproj
    -c Release
    -- --job Dry --filter "*"
```

The `--job Dry` flag runs each benchmark once with minimal warmup — enough to verify compilation and execution without waiting for statistically significant results. This catches broken benchmarks without adding significant CI time.

### Benchmark Results Documentation

```
docs/benchmarks/
├── BENCHMARKS.md          # Success criteria, run instructions, results table
└── baseline-YYYY-MM-DD/   # Timestamped BenchmarkDotNet artifacts (gitignored)
```

`BENCHMARKS.md` contains:
- How to run benchmarks locally
- Target metrics table
- Current baseline results (manually updated after each release)
- Hardware specification template for reproducibility

The `baseline-*` directories are `.gitignore`d — raw BenchmarkDotNet output is too large and machine-specific for version control. Only the summarized results in `BENCHMARKS.md` are committed.

---

## Testing Strategy

Benchmarks are not unit tests — they validate performance, not correctness. However:

- **CI dry-run** verifies all benchmark classes compile and execute without error
- **Manual execution** on consistent hardware produces baseline numbers
- **Allocation benchmarks** serve as regression tests — if `[MemoryDiagnoser]` reports non-zero allocations where zero is expected, that's a real regression

The existing 80% coverage gate is unaffected — benchmark projects are excluded from coverage (not a test project).

---

## Open Questions

1. **InternalsVisibleTo** — Some benchmarks may need access to internal types (e.g., `WorkerMetrics`, `AutoscalingEngine`). Should the benchmark project get `InternalsVisibleTo`, or should benchmarks only test through public API surfaces?

2. **Event boxing** — The event stream uses `Channel<IOrchestratorEvent>` which boxes value-type events. The allocation benchmark will quantify this. If boxing is significant, should it be addressed before baselining (changing the channel to generic) or accepted as a known cost?

3. **BenchmarkDotNet version** — Latest stable is 0.14.x. Should we pin a specific version in `Directory.Packages.props`?
