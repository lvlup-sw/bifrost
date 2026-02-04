# Bifrost Benchmarks

Performance benchmarks for the Bifrost work orchestration library, powered by [BenchmarkDotNet](https://benchmarkdotnet.org/).

## Running Benchmarks

### Full Suite

```bash
dotnet run --project src/Bifrost.Benchmarks/Bifrost.Benchmarks.csproj -c Release
```

### Filtered Run

```bash
# Run only enqueue benchmarks
dotnet run --project src/Bifrost.Benchmarks/Bifrost.Benchmarks.csproj -c Release -- --filter "*Enqueue*"

# Run only allocation benchmarks
dotnet run --project src/Bifrost.Benchmarks/Bifrost.Benchmarks.csproj -c Release -- --filter "*Allocation*"

# Run only decorator benchmarks
dotnet run --project src/Bifrost.Benchmarks/Bifrost.Benchmarks.csproj -c Release -- --filter "*Decorator*"
```

### Dry Run (CI Smoke Test)

```bash
dotnet run --project src/Bifrost.Benchmarks/Bifrost.Benchmarks.csproj -c Release -- --job Dry --filter "*"
```

### List All Benchmarks

```bash
dotnet run --project src/Bifrost.Benchmarks/Bifrost.Benchmarks.csproj -c Release -- --list flat
```

## Target Metrics

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

## Benchmark Inventory

### Core (`Bifrost.Benchmarks.Core`)

| Class | Description |
|-------|-------------|
| `EnqueueBenchmarks` | Latency of all 4 enqueue paths (`EnqueueAsync`, `TryEnqueue`, `Run`, `TryRun`) across capacity sizes |
| `WorkerThroughputBenchmarks` | End-to-end items/sec throughput with 1, 4, and 16 workers |

### Allocation Validation (`Bifrost.Benchmarks.Allocation`)

| Class | Description |
|-------|-------------|
| `EnqueueAllocationBenchmarks` | Validates zero-allocation claims for enqueue operations in steady state |
| `WorkerLoopAllocationBenchmarks` | Validates steady-state worker loop allocates nothing per item |
| `EventStreamAllocationBenchmarks` | Quantifies event struct boxing cost at the `Channel<IOrchestratorEvent>` boundary |

### Decorator Overhead (`Bifrost.Benchmarks.Decorators`)

| Class | Description |
|-------|-------------|
| `DecoratorOverheadBenchmarks` | Compares bare orchestrator vs. each decorator layer and full stack |
| `AutoscalingOverheadBenchmarks` | Isolates autoscaling metrics collection cost (Interlocked overhead) |
| `ResilienceOverheadBenchmarks` | Measures Polly pipeline overhead on the happy path |

### Autoscaling Internals (`Bifrost.Benchmarks.Autoscaling`)

| Class | Description |
|-------|-------------|
| `ScalingDecisionBenchmarks` | Measures watermark evaluation speed at different utilization levels |
| `MetricsCollectionBenchmarks` | Measures individual `WorkerMetrics` Interlocked operation costs |

## Interpreting Results

### Key Columns

| Column | Meaning |
|--------|---------|
| **Mean** | Average execution time per operation |
| **Error** | Half of the 99.9% confidence interval |
| **StdDev** | Standard deviation of measurements |
| **Allocated** | Heap memory allocated per operation (from `[MemoryDiagnoser]`) |
| **Gen0/Gen1/Gen2** | GC collections per 1000 operations |
| **Ratio** | Relative to the baseline benchmark (1.00 = same as baseline) |

### What to Watch For

- **Allocated = 0 B** for enqueue operations confirms zero-allocation hot path
- **Ratio close to 1.0** for decorator benchmarks means minimal overhead
- **Gen0 = 0** means no young-generation garbage collections triggered

## Hardware Specification

When recording baseline results, document the test hardware:

```
CPU:       [Model, clock speed, core count]
RAM:       [Size, speed]
OS:        [Name, version]
.NET:      [SDK version]
Runtime:   [.NET version]
```

## Baseline Results

Baseline results are captured after each release on consistent hardware. Raw BenchmarkDotNet artifacts are stored in `baseline-YYYY-MM-DD/` directories (gitignored). Summarized results are recorded below.

*No baseline results yet. Run benchmarks on consistent hardware and update this section.*
