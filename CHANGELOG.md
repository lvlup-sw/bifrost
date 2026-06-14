# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Breaking

The deliberate v0.5.0 break: admission outcomes move into the type system. The compiler does
the finding — every `EnqueueAsync` call site that consumed the old `ValueTask` stops compiling.

- **`EnqueueAsync` returns `ValueTask<EnqueueResult>` and gains an optional `WorkClass`** —
  was `ValueTask EnqueueAsync(TWork work, CancellationToken ct = default)`; now
  `ValueTask<EnqueueResult> EnqueueAsync(TWork work, WorkClass workClass = WorkClass.Default, CancellationToken ct = default)`.
  Admission failures never throw: capacity, watermark, and shutdown surface as a rejected result
  (`RejectionReason.CapacityExceeded` / `WatermarkExceeded` / `Shutdown`). Caller-token
  cancellation is *not* an admission failure — it surfaces as `OperationCanceledException`, the
  TAP / `ChannelWriter.WriteAsync` convention, so a caller abort stays distinct from a shut-down
  queue and is never dead-lettered. Migration:

  ```csharp
  // before (0.4.x)
  await orchestrator.EnqueueAsync(work);

  // after (0.5.0)
  var result = await orchestrator.EnqueueAsync(work);
  if (!result.IsAccepted)
  {
      // result.Reason: CapacityExceeded, WatermarkExceeded, or Shutdown
  }
  // a canceled ct throws OperationCanceledException, as with any async API
  ```

  Note the parameter order: a positional token (`EnqueueAsync(work, ct)`) no longer compiles
  because the second parameter is now `WorkClass` — pass it by name (`EnqueueAsync(work, ct: ct)`)
  or as the third argument
- **`TryEnqueue` / `Run` / `TryRun` gain an optional `WorkClass` parameter** — source-compatible
  (defaulted to `WorkClass.Default`), but binary-breaking: recompile against 0.5.0
- **`Writer` escape hatch removed** — `ChannelWriter<TWork> Writer` is gone from
  `IWorkOrchestrator<TWork>`; the internal queue is now a pluggable `DispatchStrategy` binding,
  not necessarily a `Channel`. Migrate to `EnqueueAsync`/`TryEnqueue`, which now express the same
  outcomes (acceptance, rejection, shutdown) without bypassing admission policy
- **DLQ semantics: admission rejections are dead-lettered** — a rejected enqueue now routes to
  the dead-letter pathway when a DLQ is configured: a single entry with `AttemptCount = 0` and a
  `WorkRejectedException` marker (rejection happens at admission, before any attempt, so the DLQ
  retry machinery never engages — `MaxRetries` does not apply). The caller still receives the
  rejection; the DLQ entry is observability. Previously, rejected enqueues left no trace
- **Priority strategies enqueue fail-fast** — under `UsePriorityDispatch()`, `EnqueueAsync`
  never waits for space: at capacity, or above the work class's admission watermark, it rejects
  immediately (producer-wait at capacity would reintroduce priority inversion at the admission
  boundary). The FIFO default keeps its producer-wait behavior unchanged

### Added

- **`LevelUp.Bifrost.Concurrency` package:** concurrency primitives as a leaf package (no
  dependency on other Bifrost packages) — the MultiQueue
  `ConcurrentPriorityQueue<TElement, TPriority>` (lock-striped sub-queues, two-choice relaxed
  dequeue with a documented expected rank error of `(5/6)·n`, n ≈ 4 × processor count) and the
  exact-ordering `LockingPriorityQueue<TElement, TPriority>`, ported from
  `lvlup-sw/DataFerry@2bf0456`
- **Work classes:** `WorkClass` tag (`Interactive` / `Default` / `Batch`) on every enqueue
  overload, plus an options-level classifier via `WithClassifier()` (a per-call class other than
  `Default` wins; the classifier is consulted otherwise)
- **Dispatch strategies:** `WorkOrchestratorOptions.DispatchStrategy`
  (`Fifo` default / `PriorityMultiQueue` / `PriorityLocking`) behind the `IWorkQueue<T>`
  abstraction, with the `UsePriorityDispatch()` builder extension; selection is enum/factory-based
  (no reflection, trim/AOT-safe)
- **Virtual-time priority key + watermark admission:** WFQ-style key
  `enqueueTicks − classBoost` — Interactive jumps at most the boost window (default 30s), which
  doubles as the starvation bound by construction; class-aware admission watermarks shed the
  lowest class first under pressure (Batch at 0.90 × capacity, Default at 0.95, Interactive to
  full capacity; configurable via `PriorityDispatchOptions`)
- **Queue-wait observability:** `bifrost.orchestrator.queue_wait` histogram (ms, tagged
  `work.class`) and `bifrost.orchestrator.rejected` counter (tagged `work.class` and
  `rejection.reason`) — the evidence signal for enabling priority dispatch (see the README's
  Priority Dispatch section)
- **Soak + benchmark suites:** consumer-shaped soak harness (`soak` CLI verb, nightly/manual CI
  in `.github/workflows/soak.yml`, results in `docs/benchmarks/2026-06-cpq-soak.md`), contended
  throughput sweep (`throughput` verb), CPQ latency benchmarks, and the FIFO no-regression gate
  (`docs/benchmarks/2026-06-cpq-orchestrator-baseline.md`)

## [0.4.0] - 2026-03-15

### Added

- **Handler Registration API:** `WithHandler<TWork, THandler>()` with type-based, factory, and delegate overloads for flexible handler registration
- **Scoped Handler Support:** `ScopedHandlerProxy` for per-item DI scope isolation (EF Core DbContext, etc.)
- **DLQ Extensibility:** `IDeadLetterSubscriber<TWork>`, `WithDeadLetterSubscriber()`, and multi-subscriber notifier for dead letter queue extensibility
- **EventStream + DLQ Wiring:** Dead-lettered items automatically publish to the event stream
- **DrainAsync:** Graceful drain support for zero-downtime deployments
- **Handler Decorator Extensibility:** `WithHandlerDecorator()` for custom cross-cutting concerns

### Improved

- `DeadLetterNotifier` now logs subscriber errors instead of silently swallowing
- `EventStreamOrchestrator` is drain-safe for late subscribers
- Comprehensive XML documentation for resilience/DLQ interaction model

### Stats

- 38 files changed, 609 tests, 92.91% coverage

## [0.3.5] - 2026-03-14

### Fixed

- **Resilience:** Removed `InvalidOperationException` from transient exception list — it caused business logic errors (e.g. "Queue is full") to be retried by resilience policies
- **Resilience:** Changed decorator order from 50 to 25 to avoid collision with event stream decorator, enabling both features to be composed together
- **Metrics:** Removed dead `PendingWorkCount`, `CalculateUtilizationRatio`, `RecordDequeue` from `IWorkerMetrics` — never called in production, utilization is tracked via `AutoscalingCoordinator` which reads the channel directly
- **Events:** `WorkCompletedEvent<TWork>` now implements `ICorrelatedEvent` with optional `CorrelationId`, matching `WorkEnqueuedEvent` and `WorkDeadLetteredEvent`
- **DLQ:** Removed dead no-op `await` in `DeadLetterQueue.ReadAllAsync`
- **Autoscaling:** Remediated 31 findings across autoscaling, DLQ, and event stream subsystems (PR #12)
- **Docs:** Documented placeholder metrics (`QueuedCount`, `QueuedEventCount`, `ActiveSubscriberCount`) as stubs in port interface docs

## [0.3.0] - 2026-03-02

### Added

- **Dead Letter Queue** - Failed work items are routed to a dead letter queue instead of being silently dropped
  - `IDeadLetterQueue<TWork>` abstraction in Bifrost.Core with `EnqueueAsync`, `ReadAllAsync`, and `Count`
  - `DeadLetteredWork<TWork>` record capturing exception, attempt count, failure time, and correlation ID
  - `DeadLetterQueue<TWork>` channel-backed implementation with configurable capacity
  - `DeadLetterHandler<TWork>` handler decorator with retry-then-dead-letter routing
  - `.WithDeadLetterQueue()` builder extension for fluent configuration
  - `WorkDeadLetteredEvent<TWork>` event type for event stream integration
  - `DeadLetterQueueHealthCheck` with three-tier status (Healthy / Degraded / Unhealthy)
  - `orchestrator.items.deadlettered` counter and `orchestrator.dlq.depth` gauge in OpenTelemetry metrics
  - DLQ benchmarks (`DeadLetterQueueBenchmarks`) covering enqueue, drain, happy path, and failure path

## [0.2.0] - 2026-02-07

### Added

- **Benchmark Suite** (`Bifrost.Benchmarks`) - BenchmarkDotNet performance validation infrastructure
  - Core enqueue and throughput benchmarks
  - Allocation validation benchmarks (zero-alloc hot path verification)
  - Decorator overhead benchmarks
  - Autoscaling benchmarks
  - Benchmark documentation with run instructions and baseline results
  - CI smoke test (`--job Dry`) for benchmark validation

### Changed

- Eliminated event stream struct boxing for zero-allocation event dispatch
- Fixed `Run_Sync` benchmark and documented `EnqueueAsync` capacity behavior

### Fixed

- CI formatting check and format enforcement
- Test timing reliability improvements

## [0.1.0] - 2026-02-02

### Added

- **LevelUp.Bifrost.Core** - Core primitives and contracts for channel-based work orchestration
  - `Result<T>` and `Error` types for functional error handling
  - `IProcessExecutor<T>` contract for process execution
  - `ProcessConfiguration` and `AutoscalingOptions` for configuration
  - Event types for streaming (`EventMessage`, `EventData`, `ProgressEventData`)

- **LevelUp.Bifrost** - Main library with work orchestration infrastructure
  - `TaskOrchestrator` - Background task scheduling with resilience
  - `AutoscalingEngine` - Dynamic worker scaling based on queue utilization
  - `ResiliencyPolicyGenerator` - Polly policy factory for retry/timeout/circuit breaker
  - `WorkerRegistry` - Tracks active workers for autoscaling

- **LevelUp.Bifrost.Resilience** - Polly integration for resilient operations
  - Pre-configured resilience policies
  - Circuit breaker patterns
  - Retry with exponential backoff

- **LevelUp.Bifrost.HealthChecks** - Health check implementations
  - Autoscaling health checks
  - Event streaming health checks
  - Worker registry health checks

- **LevelUp.Bifrost.OpenTelemetry** - Observability integration
  - Metrics for work orchestration
  - Tracing support
  - Diagnostic listeners

### Changed

- Renamed from `Levelup.Channels` to `Bifrost` per naming convention decision
- Restructured repository to follow lvlup-sw conventions (src/ layout)

[Unreleased]: https://github.com/lvlup-sw/bifrost/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/lvlup-sw/bifrost/compare/v0.3.5...v0.4.0
[0.3.5]: https://github.com/lvlup-sw/bifrost/compare/v0.3.0...v0.3.5
[0.3.0]: https://github.com/lvlup-sw/bifrost/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/lvlup-sw/bifrost/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/lvlup-sw/bifrost/releases/tag/v0.1.0
