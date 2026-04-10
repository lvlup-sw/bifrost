# Design: Bifrost Durable Scheduling API

**Feature ID:** `durable-scheduling-api`
**Related:** [issue #16](https://github.com/lvlup-sw/bifrost/issues/16), `lvlup-sw/basileus#145`, `lvlup-sw/basileus#143`

## Problem Statement

Bifrost ships excellent resilient background execution for `Func<Task>` work items but has **no scheduling primitives**. Consumers that need "run this work every N minutes" or "fire this work at time T" must build it themselves or adopt a separate library.

The immediate forcing use case is `basileus#145` — a scheduled ingestion job for `SemanticDocument` ObjectSets — which currently lives in a hybrid: WolverineFx handles scheduling via a self-rescheduling message, and each tick enqueues a `Func<Task>` into Bifrost for execution. This splits scheduling and execution across two libraries and pushes consumers to reason about two sets of retry/DLQ/observability primitives for one logical job.

WolverineFx 5.x does not solve the problem cleanly either. It supports durable one-shot `ScheduleAsync(msg, when)` via the Marten/Postgres outbox, but lacks cron support, lacks a recurring-job primitive (idiomatic Wolverine is the self-rescheduling message pattern, which requires idempotent seeding to avoid pyramid scheduling), lacks a runtime job registry (`Pause`/`Resume`/`Trigger`/`GetJobs`), lacks missed-fire policies, and explicitly warns that outbox-backed scheduling "is meant for relatively low numbers of messages." See [issue #16](https://github.com/lvlup-sw/bifrost/issues/16) for the full feature inventory.

Bifrost's opportunity is to differentiate on exactly those gaps while staying tightly scoped: the library owns *when* and *how* work fires and how it's observed, and delegates durable storage and multi-instance coordination to adapter packages that integrate with standard .NET storage abstractions (Marten/Critter Stack, PostgreSQL).

## Chosen Approach

**Registry + Timer-Wheel Engine + Pluggable Dispatch + Pluggable Store.**

A central `IScheduleRegistry` owns job metadata and exposes both a DI-time fluent API (the common path) and a runtime registration API (the escape hatch for dynamic jobs). A single background loop drives a min-heap priority queue keyed by `NextFireAt`, dispatches due jobs through a pluggable `IJobDispatcher`, and persists state through a pluggable `IScheduleStore`. Multi-instance coordination is handled by an opt-in `IExclusiveScheduleStore` capability — the core ships single-instance always-leader; adapter packages ship durable multi-instance stores that integrate with Marten/Postgres directly rather than owning a schema in-house.

This shape was chosen in preference to three alternatives considered during brainstorming (see [Exploration](#exploration-summary) below): a scan-based polling loop (simpler but concedes the high-volume differentiation Wolverine's outbox model cannot provide), per-job `PeriodicTimer` (trivial at tiny scale, fails at 1K+ jobs), and coupling scheduling directly into `IWorkOrchestrator` as a decorator (awkward for multi-type registries because each orchestrator is typed to a single `TWork`).

The selected approach hits the three design pressures simultaneously:

1. **Forward-compatible API** — Storage, dispatch, and coordination are all behind interfaces. v1 ships an in-memory-only implementation. Follow-up releases add durable stores and interop adapters without breaking the v1 API.
2. **Tightly scoped core** — The core `Bifrost.Scheduling` package owns contracts, the tick engine, the registry, the fluent builder, observability, and an in-memory store. It does *not* own any persistent schema. Marten/Postgres/SQLite integration lives in separate adapter packages.
3. **High-volume differentiation** — The timer-wheel engine gives O(log n) fire cost, sub-second precision, and zero-allocation steady state. The store contract is checkpoint-oriented (not per-tick), so a future `Bifrost.Scheduling.Marten` adapter can batch state writes instead of hitting the DB on every fire.

## Requirements

### DR-1: Job registry with fluent and runtime APIs

The scheduler must expose a central registry (`IScheduleRegistry`) where jobs can be declared at DI time (fluent builder, static jobs) and registered/unregistered dynamically at runtime. The registry is the single source of truth for job metadata — name, cadence, dispatch target, missed-fire policy, state.

**Acceptance criteria:**
- Given a DI-time registration via `services.AddScheduler(s => s.AddJob<IngestJob>("ingest-docs").Every(5.Minutes()))`
  When the host starts
  Then `IScheduleRegistry.GetJob("ingest-docs")` returns a `JobDescriptor` with cadence `Interval(5m)` and state `Running`.
- Given a running scheduler
  When a caller invokes `registry.RegisterAsync("adhoc", Cadence.Interval(30.Seconds()), dispatch, ct)`
  Then `GetJobs()` includes `"adhoc"` and the tick loop begins firing it on the next scheduled time.
- Given a registered job
  When `registry.PauseAsync("ingest-docs")` is called
  Then subsequent ticks do not dispatch the job until `ResumeAsync("ingest-docs")` is called, and `GetJob("ingest-docs").State == Paused`.
- Given a registered job
  When `registry.TriggerAsync("ingest-docs")` is called
  Then the job is dispatched immediately regardless of `NextFireAt`, and the subsequent fire schedule is unchanged.
- Registering a job with a duplicate name throws `DuplicateJobNameException`.
- Job names must match `^[a-z0-9][a-z0-9-_.]{0,127}$` — enforced at registration time.

### DR-2: Cadence primitives

The scheduler must support the cadence types the issue calls out as absent in Wolverine: interval, cron (Cronos-backed), one-shot at absolute time, one-shot with delay, and jittered interval.

**Acceptance criteria:**
- `Cadence.Interval(TimeSpan)` — fires at a fixed interval, starting at `now + interval` on first registration.
- `Cadence.Cron(string expression, TimeZoneInfo? tz = null)` — fires per cron schedule, defaulting to UTC. Invalid cron expressions throw at registration time (fail-fast).
- `Cadence.At(DateTimeOffset)` — one-shot, fires once at the specified absolute time, then the job is removed from the registry.
- `Cadence.After(TimeSpan)` — one-shot, fires once `now + delay` from registration.
- `Cadence.Interval(TimeSpan).WithJitter(double fraction)` — fires at `interval ± (interval * fraction * random)`. `fraction` must be in `[0, 1]`.
- Given a cadence
  When the scheduler asks for `cadence.ComputeNextFire(lastFiredAt, now)`
  Then it returns a `DateTimeOffset` strictly greater than `now` (except for `At` cadences that have already fired, which return `null`).

### DR-3: Missed-fire policies

The scheduler must support declarative missed-fire policies evaluated on startup recovery and on resume-after-pause. This is the behavior Wolverine's self-rescheduling message pattern forces consumers to reinvent per job.

**Acceptance criteria:**
- `MissedFirePolicy.Coalesce` (default): if multiple fire times were missed, fire exactly once to catch up, then resume normal scheduling.
- `MissedFirePolicy.FireAllMissed`: if N fire times were missed, fire the job N times back-to-back, then resume normal scheduling. Catches up every missed window.
- `MissedFirePolicy.SkipMissed`: if any fire times were missed, skip them entirely and schedule the next fire at the next future fire time.
- Given a job with `Interval(1.Minutes())` last fired at `T-5min`, policy `Coalesce`, and current time `T`
  When the scheduler starts or the job resumes
  Then the job fires exactly once (the coalesced catch-up) and its next fire is scheduled for `T + 1min`.
- Given the same job with policy `FireAllMissed`
  When the scheduler starts
  Then the job dispatches 5 times in quick succession, then resumes at the normal interval.
- The missed-fire policy is evaluated in the tick loop (not the store) so all store implementations inherit the behavior uniformly.

### DR-4: Pluggable dispatch — orchestrator, inline, and custom

Job execution must be decoupled from job scheduling. A job declares a dispatch target that decides *how* work runs once the scheduler decides *when*. Three dispatch modes ship in v1.

**Acceptance criteria:**
- **Orchestrator dispatch:** Given a job configured with `.DispatchTo<IWorkOrchestrator<IngestWork>>(fire: ctx => new IngestWork(ctx.FireTime))`
  When the job fires
  Then the scheduler calls `orchestrator.EnqueueAsync(work, ct)` and the work inherits the orchestrator's resilience / DLQ / autoscaling / metrics chain.
- **Inline dispatch:** Given a job configured with `.Run(async (ctx, ct) => await cache.RefreshAsync(ct))`
  When the job fires
  Then the delegate is invoked on a pool thread (not the tick thread) and its `Task` is tracked by the scheduler for idle-wait semantics.
- **Custom dispatch:** Given a job configured with `.DispatchVia<TDispatcher>()` where `TDispatcher : IJobDispatcher`
  When the job fires
  Then `DispatchAsync(JobFireContext, ct)` is resolved from DI and invoked. This is the extension point for future adapters (e.g., `BifrostToWolverineDispatcher`).
- The tick thread never `await`s user code directly. All dispatch modes hand off to a pool thread before the scheduler records the fire, so a slow dispatcher cannot stall the tick loop.
- Dispatch failures are surfaced through the fire event (`JobFireFailedEvent`) but do not crash the scheduler. The job's next fire time is still computed and scheduled.

### DR-5: Pluggable storage via `IScheduleStore`

The scheduler must persist registry state and fire checkpoints through a minimal `IScheduleStore` contract. v1 ships an `InMemoryScheduleStore` (noop persist). The contract must be shape-compatible with later Marten/Postgres adapters — storage is a **capability**, not a feature Bifrost owns.

**Acceptance criteria:**
- `IScheduleStore.LoadAllAsync(ct)` is called once at scheduler startup and returns the full set of `JobRecord` entries to seed the registry.
- `IScheduleStore.SaveAsync(JobRecord, ct)` is called on registry mutation (register, pause, resume, unregister).
- `IScheduleStore.RecordFiredAsync(name, firedAt, nextFireAt, ct)` is called after each successful fire-dispatch handoff (not after dispatch completion). The store decides internally whether to batch or write-through.
- `IScheduleStore.DeleteAsync(name, ct)` removes a job entirely.
- Given `InMemoryScheduleStore` (v1 default)
  When any store method is called
  Then the call completes synchronously and no I/O occurs — but registry state is still authoritative and observable.
- The contract must support a future `Bifrost.Scheduling.Marten` adapter that stores `JobRecord` as a Marten document, without the scheduler core depending on Marten types.
- The core `Bifrost.Scheduling` package must not reference `Microsoft.Data.Sqlite`, `Npgsql`, `Marten`, or any persistence library.

### DR-6: Coordination — single-leader default, opt-in exclusivity

The scheduler must ship single-instance by default (always-leader) and must support multi-instance deployments through an opt-in `IExclusiveScheduleStore` capability that extends `IScheduleStore` with leader-election semantics. Core Bifrost does not implement leader election — adapter packages do.

**Acceptance criteria:**
- Given the default in-memory store on a single process
  When the scheduler starts
  Then it assumes leadership and ticks every registered job.
- Given three app instances all configured with `IScheduleStore` (non-exclusive)
  When all three start
  Then all three tick independently and duplicate fires occur. The scheduler logs a warning at startup if `IScheduleStore` does not also implement `IExclusiveScheduleStore` and the configuration requests multi-instance mode.
- Given three app instances configured with a hypothetical future `MartenExclusiveScheduleStore`
  When all three start
  Then exactly one instance acquires leadership via the store's `TryAcquireLeadershipAsync(ct)` method and only the leader ticks jobs. On leader loss or shutdown, another instance acquires leadership.
- The `IExclusiveScheduleStore` contract ships in the core package as an optional interface, but no concrete multi-instance implementation ships in v1.
- Documentation must clearly state: "For multi-instance deployments, use an adapter package that implements `IExclusiveScheduleStore` (e.g., `Bifrost.Scheduling.Marten`, not yet shipped), or run Bifrost scheduling on exactly one process."

### DR-7: Timer-wheel tick engine

The scheduler core must use a min-heap priority queue keyed by `NextFireAt`, driven by a single background loop that sleeps until the next due job. Registry mutations wake the loop via an internal `Channel<RegistryCommand>`. This is the high-volume path that beats Wolverine's outbox-backed scheduling.

**Acceptance criteria:**
- Given 10,000 jobs with randomly distributed next-fire times
  When the tick engine is steady state
  Then per-fire overhead is `O(log n)` (measurable via `BenchmarkDotNet` — see DR-11).
- Given a running tick loop with the next job due in 5 seconds
  When a new job is registered with `NextFireAt = now + 1s`
  Then the tick loop wakes within 10ms and the new job is inserted into the heap without the in-flight sleep blocking it.
- The tick thread must never `await` user code. Dispatch handoff uses `Task.Run` (inline dispatch) or `orchestrator.EnqueueAsync` (which is fast and non-blocking by contract).
- Steady-state tick loop allocations must be zero — no per-fire `List`, `Task`, or closure allocations on the hot path. Validated via `BenchmarkDotNet` `[MemoryDiagnoser]`.
- The heap is a `PriorityQueue<JobHandle, DateTimeOffset>` (from `System.Collections.Generic`), not a custom data structure.
- The scheduler uses `TimeProvider` for all time operations (no `DateTimeOffset.UtcNow`, no `Task.Delay` without `TimeProvider`) to support `FakeTimeProvider` in tests.

### DR-8: Observability — metrics, events, and health checks

The scheduler must emit OpenTelemetry metrics, publish events through Bifrost's existing event-stream infrastructure, and expose a health check. This is the "observability primitives for scheduled work" gap the issue identifies.

**Acceptance criteria:**
- Metrics exposed via `System.Diagnostics.Metrics` under the meter name `Bifrost.Scheduling`:
  - `bifrost.scheduling.jobs.registered` (updown counter) — current registry size
  - `bifrost.scheduling.jobs.fired` (counter) — total successful fires, tagged by `job.name`
  - `bifrost.scheduling.jobs.fire_latency` (histogram) — time between `NextFireAt` and actual fire dispatch
  - `bifrost.scheduling.jobs.missed_fires` (counter) — fires handled by missed-fire policy, tagged by `job.name`, `policy`
  - `bifrost.scheduling.jobs.dispatch_failures` (counter) — dispatch exceptions, tagged by `job.name`, `exception.type`
- Events published on the scheduler's event stream (same pattern as `IEventStreamOrchestrator`):
  - `JobFiredEvent(JobName, FiredAt, NextFireAt)`
  - `JobFireFailedEvent(JobName, FiredAt, Exception)`
  - `JobMissedFireEvent(JobName, MissedCount, Policy)`
  - `JobRegisteredEvent(JobName)` / `JobUnregisteredEvent(JobName)`
  - `JobPausedEvent(JobName)` / `JobResumedEvent(JobName)`
- Health check in `Bifrost.HealthChecks.Scheduling` reports `Unhealthy` if (a) the tick loop has not ticked in 3× the expected interval, or (b) dispatch failure rate exceeds a configurable threshold. Default threshold: 50% of the last 100 fires.
- `IBifrostScheduleInspector` API (read-only): `GetJobs()`, `GetJob(name)`, `GetMetricsSnapshot()`. This is the hook for future tooling (dashboards, admin UIs) to introspect the scheduler without mutating it.

### DR-9: Testing primitives — FakeTimeProvider + deterministic fire control

The scheduler must expose testing primitives that let consumers drive scheduled work deterministically. This closes the gap where Wolverine's `PlayScheduledMessagesAsync` exists but covers only one-shot scheduling.

**Acceptance criteria:**
- The scheduler accepts a `TimeProvider` via DI. In tests, consumers inject `Microsoft.Extensions.Time.Testing.FakeTimeProvider`.
- `ISchedulerTestHarness` API (only wired up when a fake `TimeProvider` is detected):
  - `AdvanceAsync(TimeSpan)` — advances the fake clock and synchronously waits for all jobs that become due to dispatch and their in-flight dispatches to complete.
  - `FireDueJobsAsync()` — dispatches all currently-due jobs without advancing time (for testing `Trigger`-style scenarios).
  - `WaitForIdleAsync(TimeSpan timeout)` — waits until the tick loop is sleeping with no pending dispatches.
- Given a test with `FakeTimeProvider` at `T0`, a job scheduled at `Interval(5.Minutes())`, and harness `AdvanceAsync(10.Minutes())`
  When the advance completes
  Then the job has fired exactly twice (deterministic, order-stable, no sleeps in the test).
- The harness must work with all dispatch modes: orchestrator, inline, and custom.
- Tests using the harness must not require `Thread.Sleep` or `Task.Delay` with real wall-clock timing.

### DR-10: Error handling and edge cases

**Acceptance criteria:**
- **Clock skew / non-monotonic time:** If `TimeProvider.GetUtcNow()` returns a value earlier than the previous reading (system clock went backwards), the scheduler logs a warning and continues using the new reading. No fire is lost; no fire is duplicated.
- **Tick loop exception:** If the tick loop itself throws (not a dispatch — the loop's own code), the scheduler logs a critical error, emits `SchedulerFaultedEvent`, and attempts to restart the loop. After 3 consecutive restart failures within 60 seconds, the scheduler transitions to `Faulted` state, stops ticking, and the health check reports `Unhealthy`.
- **Dispatch exception:** A dispatcher that throws is logged, tagged on `dispatch_failures` metric, and published as `JobFireFailedEvent`. The job's next fire time is still computed and scheduled — a failed fire does not break the schedule.
- **Job handler timeout:** Inline dispatch respects the cancellation token. If the user's delegate does not honor cancellation, the scheduler does not wait for it — the fire is considered dispatched the moment the delegate is invoked.
- **Registration race:** Concurrent `RegisterAsync("same-name", ...)` calls: exactly one succeeds; all others throw `DuplicateJobNameException`. Enforced via the registry command channel (single-writer semantics).
- **Store failure on save:** `IScheduleStore.SaveAsync` throwing during registration: the registration is rolled back and the exception is propagated to the caller. The in-memory state stays consistent with the store.
- **Store failure on checkpoint:** `IScheduleStore.RecordFiredAsync` throwing after dispatch: the failure is logged and metric-tagged, but the next fire is still computed and scheduled. On next restart, the missed-fire policy handles the gap (this is the contract: checkpoints are best-effort, policies close the loop).
- **Graceful shutdown:** On `IHostedService.StopAsync`, the tick loop stops accepting new fires, waits up to the configured shutdown timeout for in-flight dispatches to complete, and then returns. In-flight dispatches that do not honor cancellation are abandoned.
- **Empty registry:** A scheduler with no jobs starts normally and the tick loop idles indefinitely (no busy wait). Registering the first job wakes the loop via the command channel.

### DR-11: Performance and allocation targets

The scheduler must meet performance targets consistent with Bifrost's existing zero-allocation goals and hit the high-volume differentiation from issue #16.

**Acceptance criteria:**
- `EnqueueFireAsync` (scheduler → dispatcher handoff) — steady-state allocations: 0 B per fire (validated by `BenchmarkDotNet` with `[MemoryDiagnoser]`).
- Tick loop steady state with 10K jobs — allocations per fire: 0 B.
- Register a new job — allocations: < 256 B (one `JobRecord` + heap insertion).
- Fire latency (time between `NextFireAt` and dispatch handoff), p99 at 10K jobs, in-memory store: < 5ms.
- Benchmarks added to `Bifrost.Benchmarks` under `Scheduling/` directory:
  - `RegistryRegistrationBenchmarks` — register, unregister, pause, resume
  - `TickEngineBenchmarks` — fire latency and allocation under load (Params: `JobCount = [10, 100, 1000, 10000]`)
  - `CadenceComputeBenchmarks` — `ComputeNextFire` for interval, cron, jittered
  - `DispatchBenchmarks` — orchestrator vs inline vs custom dispatch overhead

## Technical Design

### Package layout

The scheduler is isolated into a new package so the core `Bifrost` package remains unaffected for consumers who don't need scheduling.

```
src/
├── Bifrost.Scheduling.Core/       # Contracts: IScheduleStore, IScheduleRegistry,
│   │                              # Cadence, JobRecord, IJobDispatcher,
│   │                              # IExclusiveScheduleStore, events, metrics names
│   └── Bifrost.Scheduling.Core.csproj
│
├── Bifrost.Scheduling/            # Tick engine, InMemoryScheduleStore,
│   │                              # fluent builder, DI extensions, health check,
│   │                              # test harness. Depends on Bifrost.Scheduling.Core
│   │                              # and Bifrost (for IWorkOrchestrator dispatch).
│   └── Bifrost.Scheduling.csproj
│
└── Bifrost.Scheduling.Marten/     # Deferred to follow-up release.
                                    # IScheduleStore + IExclusiveScheduleStore
                                    # implemented on Marten document session.
```

The split between `Bifrost.Scheduling.Core` (contracts) and `Bifrost.Scheduling` (engine + default store) mirrors the existing `Bifrost.Core` / `Bifrost` split. Adapter packages depend only on `Bifrost.Scheduling.Core`, so a consumer using the Marten adapter does not transitively pull in the in-memory tick engine if they swap the whole scheduler implementation. (In practice almost everyone will use the engine — but the split keeps the option open.)

### Type model

```csharp
// Bifrost.Scheduling.Core

public abstract record Cadence
{
    public abstract DateTimeOffset? ComputeNextFire(
        DateTimeOffset? lastFiredAt,
        DateTimeOffset now);

    public static Cadence Interval(TimeSpan interval) => new IntervalCadence(interval);
    public static Cadence Cron(string expression, TimeZoneInfo? tz = null) => new CronCadence(expression, tz);
    public static Cadence At(DateTimeOffset when) => new OneShotCadence(when);
    public static Cadence After(TimeSpan delay) => new OneShotCadence(DateTimeOffset.UtcNow + delay);
}

public sealed record IntervalCadence(TimeSpan Interval, double Jitter = 0) : Cadence { ... }
public sealed record CronCadence(string Expression, TimeZoneInfo? TimeZone) : Cadence { ... }
public sealed record OneShotCadence(DateTimeOffset FireAt) : Cadence { ... }

public enum MissedFirePolicy { Coalesce, FireAllMissed, SkipMissed }
public enum JobState { Running, Paused, Faulted, Completed }

public sealed record JobRecord(
    string Name,
    Cadence Cadence,
    MissedFirePolicy MissedFirePolicy,
    JobState State,
    DateTimeOffset? LastFiredAt,
    DateTimeOffset? NextFireAt,
    string DispatchKind,           // "orchestrator" | "inline" | "custom"
    string? DispatcherTypeName,    // for custom dispatch
    IReadOnlyDictionary<string, string> Metadata);

public interface IScheduleStore
{
    ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct);
    ValueTask SaveAsync(JobRecord job, CancellationToken ct);
    ValueTask RecordFiredAsync(string jobName, DateTimeOffset firedAt, DateTimeOffset? nextFireAt, CancellationToken ct);
    ValueTask DeleteAsync(string jobName, CancellationToken ct);
}

public interface IExclusiveScheduleStore : IScheduleStore
{
    ValueTask<ILeadershipLease?> TryAcquireLeadershipAsync(CancellationToken ct);
}

public interface ILeadershipLease : IAsyncDisposable
{
    bool IsHeld { get; }
    ValueTask<bool> RenewAsync(CancellationToken ct);
}

public interface IJobDispatcher
{
    ValueTask DispatchAsync(JobFireContext context, CancellationToken ct);
}

public readonly record struct JobFireContext(
    string JobName,
    DateTimeOffset FireTime,
    DateTimeOffset? NextFireAt,
    IServiceProvider Services);
```

### Tick loop (pseudo-code)

```csharp
internal sealed class ScheduleTickLoop : BackgroundService
{
    private readonly PriorityQueue<JobHandle, DateTimeOffset> _heap = new();
    private readonly Channel<RegistryCommand> _commands;
    private readonly TimeProvider _time;
    private readonly IScheduleStore _store;
    private readonly IJobDispatcherRouter _router;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SeedFromStoreAsync(stoppingToken);
        ApplyMissedFirePoliciesOnStartup();

        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait until either: (a) the top-of-heap is due, or (b) a registry command arrives.
            var nextFire = _heap.TryPeek(out _, out var next) ? next : DateTimeOffset.MaxValue;
            var delay = nextFire - _time.GetUtcNow();

            if (delay > TimeSpan.Zero)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var waitForCommand = _commands.Reader.WaitToReadAsync(cts.Token).AsTask();
                var waitForTime = Task.Delay(delay, _time, cts.Token);
                await Task.WhenAny(waitForCommand, waitForTime);
                cts.Cancel();
            }

            DrainCommands();     // apply Register/Unregister/Pause/Resume/Trigger
            DispatchDueJobs();   // pop all entries with NextFireAt <= now
        }
    }

    private void DispatchDueJobs()
    {
        var now = _time.GetUtcNow();
        while (_heap.TryPeek(out var handle, out var nextFire) && nextFire <= now)
        {
            _heap.Dequeue();
            var nextNext = handle.Cadence.ComputeNextFire(lastFiredAt: now, now);
            _ = _router.DispatchAsync(handle, now);        // fire-and-forget to pool
            _ = _store.RecordFiredAsync(handle.Name, now, nextNext, default);

            if (nextNext is { } n)
                _heap.Enqueue(handle, n);
            // else: one-shot job, let it fall off the heap
        }
    }
}
```

### Fluent builder integration

```csharp
// Extension on IServiceCollection
public static IServiceCollection AddScheduler(
    this IServiceCollection services,
    Action<ISchedulerBuilder>? configure = null)
{
    services.AddSingleton<IScheduleStore, InMemoryScheduleStore>();
    services.AddSingleton<IScheduleRegistry, ScheduleRegistry>();
    services.AddSingleton<IJobDispatcherRouter, JobDispatcherRouter>();
    services.AddHostedService<ScheduleTickLoop>();
    services.AddHealthChecks().AddCheck<SchedulerHealthCheck>("bifrost.scheduling");

    var builder = new SchedulerBuilder(services);
    configure?.Invoke(builder);
    builder.Complete();
    return services;
}

public interface ISchedulerBuilder
{
    IJobBuilder<TWork> AddJob<TWork>(string name);
    IInlineJobBuilder AddInlineJob(string name);
    ISchedulerBuilder UseStore<TStore>() where TStore : class, IScheduleStore;
}
```

## Integration Points

- **`IWorkOrchestrator<TWork>`** — the primary dispatch target. Scheduled jobs that need resilience, DLQ, autoscaling, or metrics inherit all of it by dispatching through the orchestrator chain. No changes to `IWorkOrchestrator` are required.
- **Event stream** — scheduler events flow through the same `IEventStreamOrchestrator` infrastructure. Consumers can subscribe to `JobFiredEvent` the same way they subscribe to `WorkCompletedEvent` today.
- **Health checks** — a new `SchedulerHealthCheck` registered via `AddHealthChecks().AddCheck<>()` matches the existing DLQ and orchestrator health-check patterns.
- **OpenTelemetry** — metrics use `System.Diagnostics.Metrics` under the `Bifrost.Scheduling` meter name, consistent with the `Bifrost.OpenTelemetry` conventions.
- **Benchmarks** — new `Scheduling/` directory in `Bifrost.Benchmarks` following the existing per-area layout.
- **Wolverine interop (follow-up)** — a future `Bifrost.Scheduling.Wolverine` adapter can ship a `WolverineJobDispatcher : IJobDispatcher` (Bifrost-scheduled jobs fire Wolverine messages) and a `WolverineScheduledJobSource` (consumes Wolverine scheduled envelopes and re-dispatches through Bifrost's engine). Both are extension packages; neither affects the core contracts.

## Testing Strategy

- **Unit tests** in `Bifrost.Tests.Scheduling` (new test project):
  - Cadence `ComputeNextFire` correctness for all cadence types and edge cases (DST transitions for cron, zero-jitter boundary, one-shot after-fire).
  - Registry lifecycle: register, duplicate-name rejection, pause, resume, trigger, unregister.
  - Missed-fire policy correctness under each policy.
  - Tick loop determinism using `FakeTimeProvider` + `ISchedulerTestHarness`.
  - Dispatch routing for all three dispatch kinds.
  - `IScheduleStore` contract tests (run against `InMemoryScheduleStore`; the same suite can later be run against `Bifrost.Scheduling.Marten`).
  - Error handling: clock skew, tick loop exception, dispatch exception, store save failure, store checkpoint failure, graceful shutdown.
- **Integration tests** covering the common path: scheduler + `IWorkOrchestrator<TWork>` + resilience + DLQ. Scheduled job fails N times → dead-letters → scheduler keeps firing.
- **Benchmarks** in `Bifrost.Benchmarks/Scheduling/` validating DR-11 targets. CI runs these in `--job Dry` smoke mode; full runs are manual / release-gate.
- **Coverage gate** — 80% maintained across line, branch, and method for `Bifrost.Scheduling.Core` and `Bifrost.Scheduling`, matching the existing Bifrost repo standard.

## Exploration Summary

Three tick-engine approaches were considered during Phase 2:

- **Approach A — Timer wheel + priority queue (selected).** Single background loop, min-heap keyed by `NextFireAt`, mutations via command channel. O(log n), zero-alloc steady state, hits high-volume differentiation. The test story is solved by `TimeProvider` injection + `FakeTimeProvider`.
- **Approach B — Scan-based tick loop.** Periodic scan of a job dictionary. O(n) per tick, simple to test, but concedes the high-volume differentiation the issue explicitly calls out as a Wolverine gap.
- **Approach C — Per-job `PeriodicTimer`.** Trivial code, but inconsistent checkpointing and bad at scale (1K+ timers). Loses the single coherent store-hook point that makes batched checkpointing viable. Rejected.

Four architectural dimensions were settled in Phase 1 and are not re-explored here: layered scope (design full / ship MVP), fluent + runtime registry, hybrid pluggable dispatch, and pluggable storage with single-leader default + opt-in exclusive store. See the ideate session transcript for the trade-offs on each.

## Open Questions

1. **Cron library choice.** Cronos is the .NET community default and is what the issue proposes. Should we consider Quartz.NET's expression parser instead? Cronos is smaller and has no Quartz dependency tree — leaning Cronos unless there's a compelling reason otherwise.
2. **Package naming.** `Bifrost.Scheduling` or `Bifrost.Scheduler`? The existing packages use singular nouns (`Bifrost.Resilience`, `Bifrost.OpenTelemetry`). Slight lean to `Bifrost.Scheduling` as the conceptual area, with `ScheduleTickLoop` / `IScheduleRegistry` as the types. Needs a naming pass during planning.
3. **`FakeTimeProvider` dependency.** `Microsoft.Extensions.TimeProvider.Testing` is the standard package. Should the test harness ship in `Bifrost.Scheduling` (bringing the dep transitively) or in a separate `Bifrost.Scheduling.Testing` package (consumers opt in)? Probably the latter, matching the `.Testing` package convention.
4. **Missed-fire policy on `Paused` jobs.** If a job is paused for 10 minutes with a 1-minute interval, does `Resume` fire the coalesced catch-up? Or does `Pause` reset the "missed" clock? Current inclination: `Pause` is considered intentional absence, so resume-after-pause does **not** trigger missed-fire policy (policy only triggers on startup recovery). Needs confirmation during planning.
5. **Multi-instance rollout timing.** The design ships with single-instance core. When should the first `IExclusiveScheduleStore` implementation ship — immediately as `Bifrost.Scheduling.Marten` in the same release, or in a follow-up? The `basileus#145` forcing use case is single-instance today, so the critical path doesn't need multi-instance. Recommend planning follow-up.
6. **Relationship to future priority queues (0.5.0).** The existing roadmap proposes priority queues on `IWorkOrchestrator`. Do scheduled jobs interact with priority queues — e.g., can `.DispatchTo<IPriorityWorkOrchestrator<T>>(priority: 3)` declare a scheduler-controlled priority? Likely yes, but out of scope for the scheduler v1 design.
