# Implementation Plan: Bifrost Benchmark Suite

## Source Design

Link: `docs/designs/2026-02-03-benchmark-suite.md`

## Scope

**Target:** Full design
**Excluded:** None — all 10 benchmark classes, infrastructure, CI integration, and documentation are covered.

## Summary

- Total tasks: 7
- Parallel groups: 3
- Estimated benchmark classes: 10
- Design coverage: All sections covered

## Spec Traceability

### Traceability Matrix

| Design Section | Key Requirements | Task ID(s) | Status |
|----------------|-----------------|------------|--------|
| Technical Design > Project Structure | - csproj, Program.cs, folder layout | 001 | Covered |
| Technical Design > Project File | - BenchmarkDotNet dep, project refs, IsPackable=false | 001 | Covered |
| Technical Design > Program.cs | - BenchmarkSwitcher with all 10 classes | 001 | Covered |
| Technical Design > EnqueueBenchmarks | - 4 enqueue path latency benchmarks, Params | 002 | Covered |
| Technical Design > WorkerThroughputBenchmarks | - Single/multi-worker items/sec | 002 | Covered |
| Technical Design > EnqueueAllocationBenchmarks | - Zero-alloc validation for enqueue ops | 003 | Covered |
| Technical Design > WorkerLoopAllocationBenchmarks | - Steady-state worker loop allocations | 003 | Covered |
| Technical Design > EventStreamAllocationBenchmarks | - Event struct boxing quantification | 003 | Covered |
| Technical Design > DecoratorOverheadBenchmarks | - Per-layer cost, bare vs decorated | 004 | Covered |
| Technical Design > AutoscalingOverheadBenchmarks | - Metrics enabled vs disabled | 004 | Covered |
| Technical Design > ResilienceOverheadBenchmarks | - Polly pipeline overhead | 004 | Covered |
| Technical Design > ScalingDecisionBenchmarks | - Watermark evaluation speed | 005 | Covered |
| Technical Design > MetricsCollectionBenchmarks | - Interlocked counter cost | 005 | Covered |
| Integration Points > Solution Integration | - Add to Bifrost.sln, InternalsVisibleTo | 001 | Covered |
| Integration Points > CI Integration | - --job Dry smoke test in ci.yml | 006 | Covered |
| Integration Points > Benchmark Results Docs | - BENCHMARKS.md, .gitignore for baselines | 007 | Covered |
| Open Questions > InternalsVisibleTo | - Add Bifrost.Benchmarks to InternalsVisibleTo | 001 | Covered |
| Open Questions > BenchmarkDotNet version | - Pin in Directory.Packages.props | 001 | Covered |
| Open Questions > Event boxing | - Quantified by EventStreamAllocationBenchmarks | 003 | Covered |

---

## Task Breakdown

### Task 001: Project scaffold and solution integration

**Phase:** RED → GREEN

**TDD Steps:**

1. [RED] Create `src/Bifrost.Benchmarks/Bifrost.Benchmarks.csproj` with:
   - `OutputType` = `Exe`
   - `IsPackable` = `false`
   - `NoWarn` includes `CA1822`
   - `PackageReference` to `BenchmarkDotNet`
   - `ProjectReference` to `Bifrost` and `Bifrost.Resilience`
   - Expected failure: project doesn't exist yet, solution doesn't reference it

2. [GREEN] Complete setup:
   - Add `BenchmarkDotNet` version to `src/Directory.Packages.props`
   - Add `InternalsVisibleTo Include="Bifrost.Benchmarks"` to `src/Bifrost/Bifrost.csproj`
   - Create `src/Bifrost.Benchmarks/Program.cs` with `BenchmarkSwitcher` (initially empty array — classes don't exist yet)
   - Create folder structure: `Core/`, `Allocation/`, `Decorators/`, `Autoscaling/`
   - Add project to `src/Bifrost.sln` via `dotnet sln add`
   - Verify: `dotnet build src/Bifrost.Benchmarks/Bifrost.Benchmarks.csproj` succeeds

3. [REFACTOR] Verify:
   - Solution builds cleanly: `dotnet build src/Bifrost.sln`
   - Benchmark project is not packable
   - InternalsVisibleTo is set

**Verification:**
- [ ] Project builds
- [ ] Solution builds with new project included
- [ ] BenchmarkDotNet version pinned in Directory.Packages.props
- [ ] InternalsVisibleTo configured

**Dependencies:** None
**Parallelizable:** No (foundation for all other tasks)
**Branch:** `feature/001-benchmark-scaffold`

**Files changed:**
- `src/Bifrost.Benchmarks/Bifrost.Benchmarks.csproj` (new)
- `src/Bifrost.Benchmarks/Program.cs` (new)
- `src/Directory.Packages.props` (add BenchmarkDotNet version)
- `src/Bifrost/Bifrost.csproj` (add InternalsVisibleTo)
- `src/Bifrost.sln` (add project)

---

### Task 002: Core benchmarks — enqueue latency and worker throughput

**Phase:** RED → GREEN → REFACTOR

**TDD Steps:**

1. [RED] Create benchmark class files that reference orchestrator types:
   - `src/Bifrost.Benchmarks/Core/EnqueueBenchmarks.cs`
   - `src/Bifrost.Benchmarks/Core/WorkerThroughputBenchmarks.cs`
   - Expected: compiles but has no benchmark logic yet

2. [GREEN] Implement benchmarks:
   - **EnqueueBenchmarks**: `[Params(128, 1024)]` for capacity, `[GlobalSetup]` creates orchestrator with no-op handler, benchmarks `TryEnqueue` (baseline), `EnqueueAsync`, `Run`, `TryRun`. Use large capacity + IterationSetup to drain queue between iterations.
   - **WorkerThroughputBenchmarks**: `[Params(1, 4, 16)]` for worker count, `ItemCount = 100_000`, handler signals `CountdownEvent`, benchmark measures enqueue-to-completion time.
   - Verify: `dotnet run --project src/Bifrost.Benchmarks -c Release -- --job Dry --filter "*Enqueue*"` succeeds
   - Verify: `dotnet run --project src/Bifrost.Benchmarks -c Release -- --job Dry --filter "*Throughput*"` succeeds

3. [REFACTOR] Update `Program.cs` to register both classes in `BenchmarkSwitcher`.

**Verification:**
- [ ] Both benchmarks run with `--job Dry`
- [ ] EnqueueBenchmarks has 4 benchmark methods + Params
- [ ] WorkerThroughputBenchmarks has 1 benchmark method + Params

**Dependencies:** 001
**Parallelizable:** Yes (with 003, 004, 005)
**Branch:** `feature/002-core-benchmarks`

**Files changed:**
- `src/Bifrost.Benchmarks/Core/EnqueueBenchmarks.cs` (new)
- `src/Bifrost.Benchmarks/Core/WorkerThroughputBenchmarks.cs` (new)
- `src/Bifrost.Benchmarks/Program.cs` (register classes)

---

### Task 003: Allocation benchmarks — enqueue, worker loop, event stream

**Phase:** RED → GREEN → REFACTOR

**TDD Steps:**

1. [RED] Create benchmark class files:
   - `src/Bifrost.Benchmarks/Allocation/EnqueueAllocationBenchmarks.cs`
   - `src/Bifrost.Benchmarks/Allocation/WorkerLoopAllocationBenchmarks.cs`
   - `src/Bifrost.Benchmarks/Allocation/EventStreamAllocationBenchmarks.cs`

2. [GREEN] Implement benchmarks:
   - **EnqueueAllocationBenchmarks**: `[MemoryDiagnoser]`, `[GlobalSetup]` with warmup cycles, benchmarks `TryEnqueue_ZeroAlloc` and `EnqueueAsync_ZeroAlloc`. Ensure channel has capacity so `WriteAsync` completes synchronously.
   - **WorkerLoopAllocationBenchmarks**: `[MemoryDiagnoser]`, `[GlobalSetup]` starts orchestrator + warmup of 1000 items, benchmarks `WorkerLoop_SteadyState` enqueue+process N items.
   - **EventStreamAllocationBenchmarks**: `[MemoryDiagnoser]`, `[GlobalSetup]` creates EventStreamOrchestrator with subscriber, benchmarks `TryEnqueue_WithEventStream` to quantify boxing cost at `Channel<IOrchestratorEvent>` boundary.
   - Verify: `dotnet run --project src/Bifrost.Benchmarks -c Release -- --job Dry --filter "*Allocation*"` succeeds

3. [REFACTOR] Update `Program.cs` to register all 3 classes.

**Verification:**
- [ ] All 3 benchmarks run with `--job Dry`
- [ ] MemoryDiagnoser on all classes
- [ ] Warmup phases in GlobalSetup

**Dependencies:** 001
**Parallelizable:** Yes (with 002, 004, 005)
**Branch:** `feature/003-allocation-benchmarks`

**Files changed:**
- `src/Bifrost.Benchmarks/Allocation/EnqueueAllocationBenchmarks.cs` (new)
- `src/Bifrost.Benchmarks/Allocation/WorkerLoopAllocationBenchmarks.cs` (new)
- `src/Bifrost.Benchmarks/Allocation/EventStreamAllocationBenchmarks.cs` (new)
- `src/Bifrost.Benchmarks/Program.cs` (register classes)

---

### Task 004: Decorator overhead benchmarks — decorator, autoscaling, resilience

**Phase:** RED → GREEN → REFACTOR

**TDD Steps:**

1. [RED] Create benchmark class files:
   - `src/Bifrost.Benchmarks/Decorators/DecoratorOverheadBenchmarks.cs`
   - `src/Bifrost.Benchmarks/Decorators/AutoscalingOverheadBenchmarks.cs`
   - `src/Bifrost.Benchmarks/Decorators/ResilienceOverheadBenchmarks.cs`

2. [GREEN] Implement benchmarks:
   - **DecoratorOverheadBenchmarks**: `[GlobalSetup]` creates 4 orchestrator variants (bare, +autoscaling, +event stream, full stack). `TryEnqueue_Bare` as `[Benchmark(Baseline = true)]`, plus `TryEnqueue_WithAutoscaling`, `TryEnqueue_WithEventStream`, `TryEnqueue_FullStack`.
   - **AutoscalingOverheadBenchmarks**: `[Params(32, 128, 1024)]` for capacity. Compares `TryEnqueue_MetricsEnabled` vs `TryEnqueue_MetricsDisabled`.
   - **ResilienceOverheadBenchmarks**: Creates bare and resilience-wrapped orchestrators. `EnqueueAsync_Bare` (baseline) vs `EnqueueAsync_WithResilience`. Uses default `ResiliencySettings`.
   - Verify: `dotnet run --project src/Bifrost.Benchmarks -c Release -- --job Dry --filter "*Decorator*"` succeeds
   - Verify: `dotnet run --project src/Bifrost.Benchmarks -c Release -- --job Dry --filter "*Resilience*"` succeeds

3. [REFACTOR] Update `Program.cs` to register all 3 classes.

**Verification:**
- [ ] All 3 benchmarks run with `--job Dry`
- [ ] DecoratorOverheadBenchmarks uses Baseline attribute
- [ ] ResilienceOverheadBenchmarks exercises Polly pipeline

**Dependencies:** 001
**Parallelizable:** Yes (with 002, 003, 005)
**Branch:** `feature/004-decorator-benchmarks`

**Files changed:**
- `src/Bifrost.Benchmarks/Decorators/DecoratorOverheadBenchmarks.cs` (new)
- `src/Bifrost.Benchmarks/Decorators/AutoscalingOverheadBenchmarks.cs` (new)
- `src/Bifrost.Benchmarks/Decorators/ResilienceOverheadBenchmarks.cs` (new)
- `src/Bifrost.Benchmarks/Program.cs` (register classes)

---

### Task 005: Autoscaling benchmarks — scaling decisions and metrics

**Phase:** RED → GREEN → REFACTOR

**TDD Steps:**

1. [RED] Create benchmark class files:
   - `src/Bifrost.Benchmarks/Autoscaling/ScalingDecisionBenchmarks.cs`
   - `src/Bifrost.Benchmarks/Autoscaling/MetricsCollectionBenchmarks.cs`

2. [GREEN] Implement benchmarks:
   - **ScalingDecisionBenchmarks**: `[Params(0.1, 0.5, 0.9)]` for utilization level. `[GlobalSetup]` creates `AutoscalingEngine` with simplified constructor. Benchmark calls `EvaluateScaling(currentWorkers, utilization, maxBacklog)` overload directly.
   - **MetricsCollectionBenchmarks**: `[GlobalSetup]` creates `WorkerMetrics` instance. Benchmarks `RecordEnqueue()`, `RecordDequeue()`, `CalculateUtilization()`.
   - Verify: `dotnet run --project src/Bifrost.Benchmarks -c Release -- --job Dry --filter "*Scaling*"` succeeds
   - Verify: `dotnet run --project src/Bifrost.Benchmarks -c Release -- --job Dry --filter "*Metrics*"` succeeds

3. [REFACTOR] Update `Program.cs` to register both classes.

**Verification:**
- [ ] Both benchmarks run with `--job Dry`
- [ ] Uses internal types via InternalsVisibleTo
- [ ] MetricsCollectionBenchmarks measures individual Interlocked ops

**Dependencies:** 001
**Parallelizable:** Yes (with 002, 003, 004)
**Branch:** `feature/005-autoscaling-benchmarks`

**Files changed:**
- `src/Bifrost.Benchmarks/Autoscaling/ScalingDecisionBenchmarks.cs` (new)
- `src/Bifrost.Benchmarks/Autoscaling/MetricsCollectionBenchmarks.cs` (new)
- `src/Bifrost.Benchmarks/Program.cs` (register classes)

---

### Task 006: CI integration — benchmark dry-run step

**Phase:** RED → GREEN

**TDD Steps:**

1. [RED] CI workflow doesn't include benchmark smoke test — no validation that benchmarks compile and run in CI.

2. [GREEN] Add benchmark smoke-test step to `.github/workflows/ci.yml`:
   - Add step after "Run tests with coverage" in the `build-test` job
   - Step: `dotnet run --project src/Bifrost.Benchmarks/Bifrost.Benchmarks.csproj -c Release -- --job Dry --filter "*"`
   - Named "Benchmark smoke test"
   - This verifies all benchmark classes compile and execute without error

**Verification:**
- [ ] ci.yml has benchmark smoke test step
- [ ] Step runs after build succeeds
- [ ] Uses `--job Dry` for fast execution

**Dependencies:** 001, 002, 003, 004, 005 (all benchmarks must exist)
**Parallelizable:** No (depends on all benchmark tasks)
**Branch:** `feature/006-ci-benchmark-smoketest`

**Files changed:**
- `.github/workflows/ci.yml` (add step)

---

### Task 007: Benchmark documentation — BENCHMARKS.md

**Phase:** RED → GREEN

**TDD Steps:**

1. [RED] No benchmark documentation exists — users don't know how to run benchmarks or what targets to expect.

2. [GREEN] Create `docs/benchmarks/BENCHMARKS.md` with:
   - Run instructions (full suite, filtered, dry run, list)
   - Target metrics table (from design document)
   - Benchmark class inventory (10 classes with descriptions)
   - Hardware specification template for reproducibility
   - Interpreting results section (what MemoryDiagnoser columns mean)
   - Add `docs/benchmarks/baseline-*/` to `.gitignore` (for raw BenchmarkDotNet output)

**Verification:**
- [ ] BENCHMARKS.md exists with run instructions
- [ ] Target metrics table matches design spec
- [ ] .gitignore updated for baseline directories

**Dependencies:** None (documentation can be written independently)
**Parallelizable:** Yes (with everything)
**Branch:** `feature/007-benchmark-docs`

**Files changed:**
- `docs/benchmarks/BENCHMARKS.md` (new)
- `.gitignore` (add baseline-* pattern)

---

## Parallelization Strategy

### Sequential Chain A (Foundation)
```
Task 001 (scaffold) → Tasks 002-005 (benchmarks, parallel) → Task 006 (CI)
```

### Parallel Groups

**Group 1: Foundation (must run first)**
- Task 001: Project scaffold

**Group 2: Benchmark implementation (after 001, run in parallel)**
- Task 002: Core benchmarks (enqueue + throughput)
- Task 003: Allocation benchmarks (enqueue + worker loop + events)
- Task 004: Decorator benchmarks (overhead + autoscaling + resilience)
- Task 005: Autoscaling benchmarks (scaling + metrics)
- Task 007: Benchmark documentation

**Group 3: Integration (after 002-005)**
- Task 006: CI smoke test step

```
       ┌─── 002 (Core) ────────┐
       ├─── 003 (Allocation) ──┤
001 ───┼─── 004 (Decorators) ──┼─── 006 (CI)
       ├─── 005 (Autoscaling) ─┤
       └─── 007 (Docs) ────────┘
```

## Deferred Items

| Item | Rationale |
|------|-----------|
| Event boxing fix | Quantified by benchmark 003 first — fix decision deferred to after baseline data |
| BenchmarkDotNet version | Will use latest stable at time of implementation — pinned in Directory.Packages.props |
| Baseline results | Captured manually after all benchmarks run on consistent hardware — not part of implementation |

## Completion Checklist

- [ ] All 10 benchmark classes implemented
- [ ] All benchmarks run with `--job Dry`
- [ ] Solution builds cleanly with Bifrost.Benchmarks included
- [ ] CI workflow includes benchmark smoke test
- [ ] BENCHMARKS.md documents run instructions and targets
- [ ] InternalsVisibleTo configured for benchmark project
- [ ] BenchmarkDotNet version pinned in central package management
