# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

These changes ship as 0.7.0: keyed dispatch, handler resilience, typed and scoped handler registration, fixed terminal outcomes, and automatic metrics. The [README](README.md) explains when to choose keyed dispatch, how admission and handler policies apply, and where recovery ownership begins. Migration notes: [automatic metrics](docs/migrations/2026-11-automatic-metrics.md). Release benchmarks: [keyed dispatch release check](docs/benchmarks/2026-11-25-keyed-dispatch-release.md).

### Added

- **Keyed dispatch samples** (#135). [`samples/Bifrost.AgentFramework.Sample`](samples/Bifrost.AgentFramework.Sample/README.md) runs session-keyed background turns with Microsoft Agent Framework: `WithKeyedDispatch(static t => (t.TenantId, t.SessionId), maxPendingPerKey: 8, maxConcurrencyPerKey: 1)`, `EnqueueResult` reporting, a scoped handler that awaits `AIAgent.RunAsync` with the handler token, and a `WithHandlerResilience` timeout with retries off. It uses a local fake `IChatClient` and runs offline. [`samples/Bifrost.EntityFrameworkCore.Sample`](samples/Bifrost.EntityFrameworkCore.Sample/README.md) shows `AddDbContext` with `WithHandler<IndexDocument, EfIndexHandler>(ServiceLifetime.Scoped)` over SQLite in-memory and explains the in-memory admission boundary. The Agent Framework and EF Core packages are referenced only by these samples, with versions in `samples/Directory.Packages.props`.
- **Keyed Native AOT smoke** (#135). `samples/Bifrost.Keyed.AotSmoke` publishes keyed dispatch, a scoped handler, and handler resilience with `PublishAot=true` and checks per-key order, both admission limits, the free worker beside a saturated key, and one disposed scope per attempt. Publish it with `PublishAot=true` like the scheduling and concurrency AOT smokes.
- **Release benchmarks** (#135). `DispatchPathAllocationBenchmarks` measures the allocation per item of the FIFO, exact-priority, and keyed paths. `KeyedDispatchContentionBenchmarks` measures keyed dispatch with 4 producers and 4 workers against FIFO. The FIFO and priority paths still allocate 0 B per item. Keyed dispatch allocates per key lifetime (about 40 B per item when keys drain often, about 170 B when every item has a new key) and is expected to add nothing for a key that stays busy, though no benchmark measures that case.
- **README examples are compiled and run** (#135). `ReadmeExamplesTests` holds a verbatim copy of each C# block in `README.md`, runs it, and fails when a README block has no copy.
- **`IHttpClientBuilder.AddBifrostResilienceHandler()`** — Bifrost-generated HttpClient resilience, the intended replacement for `AddStandardResilienceHandler`. Attaches a Polly v8 `ResiliencePipeline<HttpResponseMessage>` (total-request timeout → retry → circuit breaker → per-attempt timeout, with HTTP-aware transient handling) whose policy is resolved by the HttpClient's name from `BifrostHttpResilienceOptions` — a `Default` policy plus per-name `Policies` overrides. The options are bound from the `Bifrost:Resilience` configuration section automatically (source-generated binding, trim/AOT-clean), so policies are config-driven with no explicit binding in consumer code. The call is idempotent and override-safe: it strips any resilience handler already present (e.g. a blanket default inherited from `ConfigureHttpClientDefaults`) so per-client overrides never double-stack, confining the experimental `RemoveAllResilienceHandlers` (EXTEXP0001) surface to the library so consumers never write it. Apply once via `ConfigureHttpClientDefaults` for a keyed default across every client, then call it on a named client to apply that client's keyed policy — long-running clients (SSE streams, slow provisioning) tune timeout/retry purely through configuration instead of bypassing resilience.
- **`WithTaskHandler(Func<TWork, CancellationToken, Task>)`** (#134) — registers a handler from a `Task`-returning delegate or method group.
- **`WithScopedHandler(Func<IServiceProvider, TWork, CancellationToken, ValueTask>)`** (#134) — runs the delegate in a new DI scope for each handler attempt (each dead-letter retry gets a fresh scope) and disposes the scope after the attempt exits.
- **`WithKeyedDispatch<TWork, TKey>(keySelector, maxPendingPerKey, maxConcurrencyPerKey, comparer)`** (#130) — opt-in keyed admission and dispatch, for example `.WithKeyedDispatch(static t => (t.TenantId, t.SessionId), maxPendingPerKey: 8, maxConcurrencyPerKey: 1)`. Each key keeps a FIFO, ready keys share the workers round robin, and a key at its concurrency limit keeps its items queued while other keys use free workers. A key slot is freed after the whole handler pipeline exits. Admission returns at once: `CapacityExceeded` when `Capacity` items are queued across all keys, `KeyLimitExceeded` when the key has `maxPendingPerKey` queued items, and the global limit wins when both are full. Running items do not count as pending. A per-key limit above `Capacity` is allowed. Keyed dispatch forces `QueueFullMode.Reject`. `Build` rejects it next to `UsePriorityDispatch` or `WithClassifier`, and options validation (also on start) rejects a priority strategy or `Wait` mode set through options. Limits apply to one orchestrator instance.
- **`DispatchStrategy.Keyed`** (#130) — reported by `Ordering` for a keyed orchestrator. `WithKeyedDispatch` sets it. Setting it without `WithKeyedDispatch` fails options validation, and the public `WorkOrchestrator` constructor rejects it.
- **`RejectionReason.KeyLimitExceeded`** (#130) — the reason keyed dispatch uses when a work key already has its most pending items. Rejections with this reason are counted under their own `rejection.reason` tag and are not routed to the dead-letter queue.
- **`WithAdmissionResilience(Action<ResiliencySettings>?)`** (#133) — the scope-accurate name for `WithResilience`. Polly retry and timeout wrap `EnqueueAsync` only. `WithResilience` stays as the original name and behaves the same way.
- **`WithHandlerResilience(Action<HandlerResilienceOptions>?)`** (#133) — a Polly v8 pipeline around handler calls in `Bifrost.Resilience`. `AttemptTimeout` sets a cooperative per-attempt timeout; a timed-out attempt fails with `TimeoutRejectedException` and is recorded as failed. `MaxRetries` (default 0, at most 10) with the required `ShouldRetry` predicate retries failures the caller classifies as transient, after `RetryDelay` (default 200 ms, at most 1 minute, optional exponential backoff; every delay, including a doubled one, is capped at 1 minute). Nothing is retried once the handler token is cancelled by host shutdown or `AbortAsync`. The decorator sits in a reserved handler decorator order, so for every builder call order it runs inside dead-letter capture and outside every `WithHandlerDecorator` decorator and the scoped handler proxy: each attempt gets a fresh DI scope, and each decorator sees each attempt. The worker and key slot stay held across all attempts, retry delays, and scope disposals, and a handler that ignores cancellation keeps them until it exits. Attempt counts, the dead-letter `AttemptCount`, and `bifrost.orchestrator.handler.attempts` include every retry. With `WithDeadLetterQueue`, handler resilience is the one retry owner: `DeadLetterQueueOptions.MaxRetries` must be 0, and any other value fails options validation on start (or on the first resolution without a host). Repeated calls merge their configure actions and add one decorator.
- **Automatic orchestrator metrics** (#132). `WithOpenTelemetry()` now adds `bifrost.orchestrator.outcomes` (tagged by `outcome`: `Succeeded`, `Failed`, `Canceled`, `Abandoned`), `bifrost.orchestrator.handler.attempts`, `bifrost.orchestrator.deadletter.captured`, `.capture_errors`, `.evictions`, and `.notifications_dropped`, and the gauges `bifrost.orchestrator.queue.ready` and `bifrost.orchestrator.queue.blocked`. All of them are recorded without handler code. Tag values come only from fixed enum names (`work.class`, `rejection.reason`, `outcome`), so work items and work keys never become metric labels and keyed dispatch adds no series.

### Changed

- **The README is rewritten for 0.7.0** (#135). It covers when to choose FIFO, keyed, or priority dispatch, which policies act at admission and which act around the handler, the one retry owner, and where recovery ownership begins: Bifrost owns an accepted item only in memory, and the app or a durable runtime owns recovery. It links the runnable samples.
- **Autoscaling scales up on the dispatchable backlog for keyed dispatch** (#131). With `WithKeyedDispatch`, the engine compares the high watermark to the number of queued items a free worker could take now, divided by `Capacity`. Items queued behind a key at its concurrency limit do not add workers, so one hot key does not scale the pool to `MaxWorkers`. Scale-down still compares the low watermark to the total pending count, so a blocked backlog does not remove the workers that drain it. `GetUtilizationRatio()`, `PendingWorkCount`, capacity, and the drain guard still use the total pending count. Orchestrators without keyed dispatch are unchanged.
- **`WithResilience` and `WithAdmissionResilience` can be called more than once** (#133), in any combination. The configure actions merge in call order and the resilience decorator is added once. A second call used to fail at `Build` with `Duplicate decorator order(s): 25`.
- **Handler decorator orders of 1,000,000,000 and above are reserved** (#133). `WithHandlerDecorator` throws `ArgumentOutOfRangeException` for an explicit order in that band, and its automatic order ignores reserved orders, so user decorators always run inside handler resilience.
- **The duplicate-handler error message now names `WithHandler`, `WithTaskHandler`, and `WithScopedHandler`** (#134).
- **Dead-letter retries, dead-letter capture, and completion events no longer depend on builder call order** (#132). They are no longer handler decorators. The orchestrator runs them in fixed steps and records one internal outcome per accepted item: succeeded, failed, canceled, or abandoned, with handler attempts counted apart from the outcome. Observable changes:
  - Every `WithHandlerDecorator` decorator now runs inside dead-letter retries, so it sees each attempt and its exception. Before, a decorator registered after `WithDeadLetterQueue` wrapped the retries and saw one call that did not throw.
  - With `WithEventStream`, each started item publishes exactly one `WorkCompletedEvent` after its last attempt. `Success` is `false` for a failed item (also when it was dead-lettered) and for a canceled item, and `Duration` covers all attempts. Before, `WithEventStream` first gave one event per attempt, and `WithDeadLetterQueue` first gave one `Success = true` event after the capture.
  - A final failure is captured once, after the handler pipeline and its scope exit and after the key slot is released, and before the worker reports idle. An `OperationCanceledException` after the handler token is cancelled is never retried or captured.
  - If the dead-letter queue throws while it stores a final failure, the error is logged and counted as a capture error. It is no longer reported as a handler failure, and subscribers are not notified.
  - Calling `WithDeadLetterQueue` twice no longer adds a second retry layer.
  - A final handler failure is logged once by the worker (`failed to process work item`) and, when captured, once more as a warning. Before, a dead-lettered failure was logged as one error (`dead-lettered after N attempts`).
- **Processing metrics are recorded automatically** (#132). With `WithOpenTelemetry()`, `orchestrator.items.processed`, `orchestrator.items.failed`, and `orchestrator.processing.duration` are now recorded by the orchestrator from each item's terminal outcome, once per item after its last attempt. Remove manual calls to `RecordProcessed` and `RecordFailed`, which are now `[Obsolete]`, or each item is counted twice. A manual call in the handler used to count each dead-letter retry as a failure; the automatic counters count items, and attempts are counted in `bifrost.orchestrator.handler.attempts`. The duration covers all attempts and is tagged by `outcome`. A canceled item records a duration but is neither processed nor failed. `orchestrator.items.enqueued` and `orchestrator.items.deadlettered` are still manual. See `docs/migrations/2026-11-automatic-metrics.md`.
- **Abort and completion now sweep unstarted work out of the queue** (#131). `AbortAsync`, `DisposeAsync`, and the last worker's exit after `Complete` remove every queued item that no worker has started, including keyed items blocked behind a running item of the same key, and record each one as abandoned. `PendingCount` is now `0` after `AbortAsync` returns or `Completion` finishes; before, abandoned items kept counting as pending. Running handlers keep their key slot until they exit, and `DrainAsync` still runs every accepted item.

### Fixed

- **An abort no longer faults completion when a dynamic worker's handler sees the cancellation first.** A token source sets its flag before it runs its callbacks, so a handler could throw `OperationCanceledException` while the worker's retirement-linked dequeue token was still uncancelled. The worker treated that as a failure, and `AbortAsync`, `DisposeAsync`, and `Completion` threw it. It is now treated as shutdown.
- **XML doc examples call `WithHandler` with both type arguments** (#135). The examples on `WithHealthChecks`, `WithOpenTelemetry`, `AddWorkOrchestratorHostedService`, `WorkOrchestratorHostedService`, `DelegateWorkHandler`, and `CancellableDelegateWorkHandler` used `WithHandler<THandler>()`, which does not compile. `WithScopedHandler` now has an example.
- **Docs no longer say that resilience wraps handler calls** (#133). The README, the `ResilientOrchestrator` remarks, and `DeadLetterQueueOptions.MaxRetries` now say that admission resilience wraps `EnqueueAsync` only, has no circuit breaker, and does not retry handler failures. Dead-letter retries are immediate.
- **`AddBifrostResilienceHandler()` no longer retries unsafe HTTP methods.** The retry strategy now sends a request again only when its method is GET, HEAD, OPTIONS, or TRACE. POST, PUT, PATCH, DELETE, CONNECT, and custom methods are sent once, so a change the server applied before the client saw a timeout, reset, 408, 429, or 5xx is not applied twice. `MaxRetries` still sets the retry count for safe methods.

## [0.5.1] - 2026-06-22

### Added

- **`ISchedulerBuilder.ConfigureOptions(Action<SchedulerOptions>)`** (#32) — first-class configuration of the scheduler's tick-loop options at `AddScheduler` (e.g. `services.AddScheduler(b => b.ConfigureOptions(o => o.RestartBackoff = TimeSpan.FromSeconds(2)))`). The fault-recovery cross-field constraint (`RestartBackoff × MaxRestartsInWindow < RestartWindow`) is validated and fails fast with an actionable message on a bad combination.
- **`SchedulerOptions.RestartBackoff`** (#32) — the tick loop now backs off (default 1s) between *repeated* fault restarts within the restart window, so a crash loop no longer spins a core. A single transient fault still restarts immediately, and the give-up transition (`MaxRestartsInWindow` within `RestartWindow`) is unaffected.
- **`ScheduleRegistry` implements `IAsyncDisposable` and `IDisposable`** (#32) — disposing it completes the command-channel writer so the tick loop drains its backlog and stops cleanly instead of blocking on a never-completed channel.

### Changed

- **`PriorityBinding.Auto` never selects the locking dispatcher** (#46) — `Auto` (the default priority binding) now always resolves to the lock-free `MultiQueue`. The previous capacity/rank-error heuristic that could silently switch `Auto` to the coarse global-locking heap at high capacity is removed; `Locking` is reachable only via an explicit `PriorityBinding.Locking`. No public API change — behavior only. Choose `Locking` explicitly when you need strict min-key ordering at high capacity, where the relaxed `MultiQueue`'s rank accuracy decreases.

### Fixed

- **Scheduler post-MVP follow-ups** (#32) — the tick loop stops cleanly when its registry's command channel completes (no CPU spin on dispose-while-running); fault-restart backoff prevents crash-loop spin; deferred test-hygiene fixes (deterministic event-stream disposal, guarded hosted-service startup, thread-pool-floor failure check, loop teardown in `finally`, fixture nullability). In-flight double-decrement isolation and scope-fault event observability were confirmed already-correct and locked in with regression tests. `UpdateAsync` is now documented in the durable-scheduling design.

## [0.5.0] - 2026-06-16

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
  (`Fifo` default / `PriorityMultiQueue` / `PriorityLocking` / `Priority` resolution sentinel)
  behind the `IWorkQueue<T>` abstraction, with the `UsePriorityDispatch()` builder extension;
  selection is enum/factory-based (no reflection, trim/AOT-safe). The priority binding is chosen
  via `PriorityBinding { Auto, Locking, MultiQueue }` (the `binding:` argument, default `Auto`);
  `Auto` resolves at construction from processor count × capacity — the exact lock when the
  MultiQueue's expected rank error `(5/6)·n` would reach half the capacity (ordering washes out),
  the MultiQueue otherwise — and the orchestrator exposes the outcome as `ResolvedBinding`
- **Virtual-time priority key + watermark admission:** WFQ-style key
  `enqueueTicks − classBoost` — Interactive jumps at most the boost window (default 30s), which
  doubles as the starvation bound by construction under the exact-ordering locking binding (the
  relaxed MultiQueue approximates the order and treats the bound as best-effort); class-aware
  admission watermarks shed the
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
- **Durable scheduling (`LevelUp.Bifrost.Scheduling`, `.Scheduling.Core`, `.Scheduling.Testing`):**
  a virtual-time tick engine that fires jobs on a `Cadence` — `Cadence.Interval`, `Cadence.Cron`
  (Cronos-backed, optional `TimeZoneInfo`), `Cadence.At`, and `Cadence.After`, with optional jitter
  — registered via `AddScheduler()`. Jobs dispatch through the work orchestrator, run under a
  per-fire DI scope, and honor a missed-fire policy (e.g. `Coalesce`) across downtime. Ships
  scheduler lifecycle events, a `SchedulerHealthCheck`, and a deterministic `FakeTimeProvider`-driven
  `SchedulerTestHarness` (in `LevelUp.Bifrost.Scheduling.Testing`). NativeAOT-clean (AOT smoke in
  CI). Cron parsing via Cronos 0.13.0

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

[Unreleased]: https://github.com/lvlup-sw/bifrost/compare/v0.5.1...HEAD
[0.5.1]: https://github.com/lvlup-sw/bifrost/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/lvlup-sw/bifrost/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/lvlup-sw/bifrost/compare/v0.3.5...v0.4.0
[0.3.5]: https://github.com/lvlup-sw/bifrost/compare/v0.3.0...v0.3.5
[0.3.0]: https://github.com/lvlup-sw/bifrost/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/lvlup-sw/bifrost/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/lvlup-sw/bifrost/releases/tag/v0.1.0
