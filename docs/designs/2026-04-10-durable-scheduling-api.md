# Design: Bifrost Scheduling API

**Feature ID:** `durable-scheduling-api` (historical slug — see naming note)
**Related:** [issue #16](https://github.com/lvlup-sw/bifrost/issues/16), `lvlup-sw/basileus#145`, `lvlup-sw/basileus#143`
**Revised:** 2026-06-12, applying R1–R10 from `docs/research/2026-06-12-durable-scheduling-precedents.md`

> **Naming note (R6):** This feature was originally titled "durable scheduling." In 2026, "durable"
> denotes the durable-*execution* category (Temporal, Restate, Dapr Workflow, AWS Lambda Durable
> Functions) and collides head-on with Azure's managed product literally named "Durable Task
> Scheduler" — while v1 of this feature ships in-memory persistence only. Public positioning is
> therefore **"Bifrost Scheduling — pluggable schedule persistence with at-least-once recovery."**
> The feature id, branch, and file names keep the historical slug to preserve references.

## Problem Statement

Bifrost ships excellent resilient background execution for `Func<Task>` work items but has **no scheduling primitives**. Consumers that need "run this work every N minutes" or "fire this work at time T" must build it themselves or adopt a separate library.

The immediate forcing use case is `basileus#145` — a scheduled ingestion job for `SemanticDocument` ObjectSets — which currently lives in a hybrid: WolverineFx handles scheduling via a self-rescheduling message, and each tick enqueues a `Func<Task>` into Bifrost for execution. This splits scheduling and execution across two libraries and pushes consumers to reason about two sets of retry/DLQ/observability primitives for one logical job.

WolverineFx 5.x does not solve the problem cleanly either. It supports durable one-shot `ScheduleAsync(msg, when)` via the Marten/Postgres outbox, but lacks cron support, lacks a recurring-job primitive (idiomatic Wolverine is the self-rescheduling message pattern, which requires idempotent seeding to avoid pyramid scheduling), lacks a runtime job registry (`Pause`/`Resume`/`Trigger`/`GetJobs`), lacks missed-fire policies, and explicitly warns that outbox-backed scheduling "is meant for relatively low numbers of messages." See [issue #16](https://github.com/lvlup-sw/bifrost/issues/16) for the full feature inventory.

Bifrost's opportunity is to differentiate on exactly those gaps while staying tightly scoped: the library owns *when* and *how* work fires and how it's observed, and delegates durable storage and multi-instance coordination to adapter packages that integrate with standard .NET storage abstractions (Marten/Critter Stack, PostgreSQL).

## Competitive Landscape (added 2026-06-12, R7)

The original gap analysis was written against WolverineFx only. The discovery spike
(`docs/research/2026-06-12-durable-scheduling-precedents.md`) re-surveyed the field:

- **NCronJob** (MIT, active, TimeProvider-based) already ships ~60-70% of the proposed registry
  surface: fluent DI registration, runtime register/remove/update/enable/disable, trigger-now. It is
  cron-expression-only, in-memory by design, with no misfire concept, no OTel, and no AOT story.
- **TickerQ** (MIT/Apache-2.0, very active) brackets the proposal from the durable side:
  source-generator discovery, EF Core/Redis persistence, multi-node coordination, dashboard — and it
  declares `IsAotCompatible=true` on its core packages. Its catch-up is global config (not per-job),
  and its persistence layers are not AOT-clean (EF Core: officially experimental under AOT; Redis:
  open trimming bug).
- **Quartz.NET / Hangfire** remain the heavyweight incumbents; both are architecturally AOT-hostile
  (reflection job activation; expression-tree serialization). **Coravel** is ~17 months stale.

**Differentiators this design is justified by** — none achievable as "NCronJob + 50 lines of glue":

1. **Orchestrator dispatch** — scheduling as a trigger source for Bifrost's
   resilience/DLQ/autoscaling/metrics pipeline. The unique integration; the actual product.
2. **Interval and jittered cadences** — inexpressible in cron; no incumbent ships jitter.
3. **Declarative per-job missed-fire policies** — only Quartz (threshold baggage) and Hangfire
   (coarse modes) have anything comparable; a real gap in the lightweight tier.
4. **Deterministic test harness** — `AdvanceAsync`/`FireDueJobsAsync`/`WaitForIdleAsync` semantics
   exist nowhere in the field; Quartz 4 (unreleased) gets TimeProvider injection but no idle-sync.
5. **AOT end-to-end including the persistence path** (DR-13) — the claim TickerQ cannot currently make.

**When NOT to use:** teams not running work through `IWorkOrchestrator` should prefer NCronJob
(simple, in-memory) or TickerQ (durable, dashboard, multi-node). The README and package description
must say this plainly; honest positioning is part of the design.

## Chosen Approach

**Registry + Min-Heap Tick Engine + Pluggable Dispatch + Pluggable Store.**

A central `IScheduleRegistry` owns job metadata and exposes both a DI-time fluent API (the common path) and a runtime registration API (the escape hatch for dynamic jobs). A single background loop drives a min-heap priority queue keyed by `NextFireAt`, dispatches due jobs through a pluggable `IJobDispatcher`, and persists state through a pluggable `IScheduleStore`. The core ships single-instance always-leader; multi-instance coordination is deferred to the adapter follow-up **entirely, including its contract** (see DR-6 / R4) — adapter packages will ship durable multi-instance stores that integrate with Marten/Postgres directly rather than owning a schema in-house.

This shape was chosen in preference to three alternatives considered during brainstorming (see [Exploration](#exploration-summary) below): a scan-based polling loop (simpler but concedes the high-volume differentiation Wolverine's outbox model cannot provide), per-job `PeriodicTimer` (trivial at tiny scale, fails at 1K+ jobs), and coupling scheduling directly into `IWorkOrchestrator` as a decorator (awkward for multi-type registries because each orchestrator is typed to a single `TWork`).

The selected approach hits the three design pressures simultaneously:

1. **Forward-compatible API** — Storage, dispatch, and coordination are all behind interfaces. v1 ships an in-memory-only implementation. Follow-up releases add durable stores and interop adapters without breaking the v1 API.
2. **Tightly scoped core** — The core `Bifrost.Scheduling` package owns contracts, the tick engine, the registry, the fluent builder, observability, and an in-memory store. It does *not* own any persistent schema. Marten/Postgres/SQLite integration lives in separate adapter packages.
3. **High-volume headroom** — The min-heap engine gives O(log n) fire cost, sub-second precision, and an allocation-light steady state. (Per the discovery spike, raw throughput is headroom rather than the competitive axis — no incumbent competes on allocations; see DR-11/R2.) The store contract is checkpoint-oriented (not per-tick), so a future `Bifrost.Scheduling.Marten` adapter can batch state writes instead of hitting the DB on every fire.

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
- Given a registered job
  When `registry.UpdateAsync("ingest-docs", Cadence.Cron("0 */2 * * *"), MissedFirePolicy.Coalesce, ct)` is called
  Then the job's cadence and missed-fire policy are replaced, `NextFireAt` is recomputed from the new schedule alone, and the tick loop re-arms the job from the updated record — without firing it as a side effect of the write (R10). A relative one-shot is resolved against the registry's injected `TimeProvider` at update time (same contract as `RegisterAsync`), and updating to a one-shot instant already in the past throws `ArgumentOutOfRangeException` rather than firing immediately. Updating an unknown name throws `JobNotFoundException`.
- Registering a job with a duplicate name throws `DuplicateJobNameException`.
- Job names must match `^[a-z0-9][a-z0-9-_.]{0,127}$` — enforced at registration time.
- Registering a one-shot cadence whose fire time is already past (`Cadence.At(past)`) throws at
  registration — never a silent immediate fire (R10: fire-now triggered by a write op is the field's
  #1 surprise generator; quartznet#636/#2180, Hangfire#1637).
- Mutating operations never cause immediate execution as a side effect: re-registering or updating a
  job with an unchanged or future schedule does not fire it. `TriggerAsync` is the only API that
  fires on demand.
- Calling `AddScheduler` twice on the same `IServiceCollection` throws `InvalidOperationException` —
  registration APIs that look composable but silently no-op are an ergonomics trap (NCronJob#138).
- Unsupported configuration combinations fail at registration/startup with a descriptive exception,
  never an NRE at fire time (coravel#91).

### DR-2: Cadence primitives

The scheduler must support the cadence types the issue calls out as absent in Wolverine: interval, cron (Cronos-backed), one-shot at absolute time, one-shot with delay, and jittered interval.

**Acceptance criteria:**
- `Cadence.Interval(TimeSpan)` — fires at a fixed interval, starting at `now + interval` on first registration.
- `Cadence.Cron(string expression, TimeZoneInfo? tz = null)` — fires per cron schedule, defaulting to UTC. Invalid cron expressions throw at registration time (fail-fast).
- `Cadence.At(DateTimeOffset)` — one-shot, fires once at the specified absolute time, then the job is removed from the registry.
- `Cadence.After(TimeSpan)` — one-shot, fires once `now + delay`, where `now` is resolved **at
  registration time by the registry via its injected `TimeProvider`** — never inside the static
  `Cadence` factory, which cannot see the injected clock (R1; see DR-7's banned-API rule).
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

### DR-6: Coordination — single-process v1; coordination contract deferred (R4)

v1 ships single-instance only (always-leader). The coordination contract (`IExclusiveScheduleStore`
/ leadership lease) does **not** ship in v1 — not even as an unimplemented interface.

**Rationale (discovery spike):** an unimplemented coordination contract is a guess, and the
originally proposed shape exhibited the two classic flaws. `bool IsHeld` is the local-knowledge
anti-pattern from Kleppmann's fencing argument — a client can never locally know its lease is still
valid (GC pause → expired lease → two leaders). And the absence of an epoch/fencing token cannot be
retrofitted onto a shipped interface without breaking every external implementor —
`IDistributedCache` → HybridCache is the canonical .NET cautionary tale. For perspective: Quartz
clustering needs no fencing token only because its lock and state transition commit in the same DB
transaction; Wolverine/Marten's own leader election is advisory, backstopped by transactional
writes. The contract should be designed against the first real implementation, not before it.

**Acceptance criteria:**
- Given the default in-memory store on a single process, when the scheduler starts, then it ticks
  every registered job.
- Given multiple app instances configured with the same non-exclusive `IScheduleStore`, when all
  start, then all tick independently and duplicate fires occur — and each instance logs a prominent
  startup warning when configuration declares multi-instance intent without an exclusive-capable
  store.
- No coordination interface, lease type, or leader-election API appears in the v1 public surface.
- Documentation states plainly: "Run Bifrost scheduling on exactly one process. Multi-instance
  support arrives with the storage adapter (e.g., `Bifrost.Scheduling.Marten`)."

**Requirements recorded for the future contract** (binding on the adapter follow-up, not on v1):
- The lease must carry a strictly monotonic **epoch/fencing token** (Kubernetes `leaseTransitions`
  is the minimum precedent).
- Expose `LastRenewedAt`/`ExpiresAt` rather than a bare `IsHeld` bool.
- Checkpoint writes by exclusive stores must be **conditional on ownership**
  (`UPDATE … WHERE owner = @me AND epoch = @epoch`, or same-transaction lock validation,
  Quartz-style). The lease is advisory (efficiency); correctness is enforced store-side, consistent
  with the at-least-once contract (DR-12).
- Prefer an abstract base class over an interface for the lease type, preserving non-breaking
  evolution (HybridCache precedent).

### DR-7: Min-heap tick engine

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
- The heap is a `PriorityQueue<JobHandle, DateTimeOffset>` (from `System.Collections.Generic`), not a custom data structure. (Deliberately *not* a hashed/hierarchical timer wheel — Varghese & Lauck wheels are O(1) and pay off at ~100K+ timers; at this design's scale the heap is simpler and sufficient. Earlier drafts mislabeled this engine a "timer wheel" — R5.)
- **Early-wake clamp (R2):** timers can fire before the scheduled instant (`Task.Delay` wakes up to ~16ms early on Windows — NCronJob#327 was a confirmed duplicate-fire bug from exactly this). If the loop wakes early, it re-sleeps until `NextFireAt <= now`. The next fire time is always computed from the **scheduled occurrence time**, never from a wall-clock reading earlier than it. Exactly one dispatch per occurrence.
- The scheduler uses `TimeProvider` for all time operations (no `DateTimeOffset.UtcNow`, no `Task.Delay` without `TimeProvider`) to support `FakeTimeProvider` in tests. **Enforced mechanically (R1):** a banned-API check (analyzer or architecture test) fails the build on any `DateTime.Now`/`DateTime.UtcNow`/`DateTimeOffset.Now`/`DateTimeOffset.UtcNow` or non-`TimeProvider` `Task.Delay` in shipping scheduling code — NCronJob#169 shows partial TimeProvider adoption is a recurring real-world defect class, and this design's own first draft contained one (`Cadence.After`).

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
- `IBifrostScheduleInspector` API (read-only): `GetJobs()`, `GetJob(name)`, `GetNextOccurrences(name, int count)`, `GetMetricsSnapshot()`. This is the hook for future tooling (dashboards, admin UIs) to introspect the scheduler without mutating it. `GetNextOccurrences` previews upcoming fire times **computed by the same cadence engine that fires** — cron-dialect surprises recur when display and firing disagree (R10; coravel#250, Hangfire#899 where the dashboard showed a different next-execution than what ran).

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

> The 42-case mined edge-case inventory in `docs/research/2026-06-12-durable-scheduling-precedents.md` §6
> is the authoritative companion checklist for this requirement's test plan (R9). The criteria below
> incorporate its highest-leverage additions.

**Acceptance criteria:**
- **Clock skew / non-monotonic time:** If `TimeProvider.GetUtcNow()` returns a value earlier than the previous reading (system clock went backwards), the scheduler logs a warning and continues using the new reading. No fire is lost; no fire is duplicated. The loop must never sleep on a stale absolute deadline: after a backward jump it re-arms within one wait cycle (quartznet#1508/#2034: a backward clock change silently stopped ALL firing until process restart).
- **Per-job failure isolation:** one job's `ComputeNextFire` throwing (e.g., timezone conversion on an invalid local time) is logged and published as `JobFireFailedEvent`, marks that job `Faulted`, and must not halt scheduling of other jobs (Hangfire#530/#529/#537: one invalid-local-time conversion crash-looped the shared scheduler loop, killing ALL recurring jobs).
- **DST transitions:** cron cadences in DST timezones must satisfy: (a) *monotonic progress* — `ComputeNextFire(t) > t` for every instant across both DST boundaries, validated as a property test across the whole transition week in multiple zones including half-hour offsets (quartznet#2497/#332 infinite-loop class); (b) *exact fire-count in the fall-back repeated hour* — a per-minute schedule fires exactly 60 times: never a tight refire loop (quartznet#2475 — still unfixed in Quartz) and never a one-hour silence (Hangfire#567).
- **Resume fires nothing (pin test):** a job paused across N occurrences fires zero catch-up executions on resume; its next fire is the next natural occurrence. Missed-fire policies apply only on startup recovery from a store. (Decision committed in the plan; validated by the spike — Quartz's resume-applies-misfire default is a recurring user surprise.)
- **Saturation correctness:** 1,000+ jobs due at the same instant are all dispatched (or carried into the immediately following loop iterations) — none silently lost (Hangfire#751: due-job slip observed at ~700 recurring jobs).
- **Tick loop exception:** If the tick loop itself throws (not a dispatch — the loop's own code), the scheduler logs a critical error, emits `SchedulerFaultedEvent`, and attempts to restart the loop. After 3 consecutive restart failures within 60 seconds, the scheduler transitions to `Faulted` state, stops ticking, and the health check reports `Unhealthy`.
- **Dispatch exception:** A dispatcher that throws is logged, tagged on `dispatch_failures` metric, and published as `JobFireFailedEvent`. The job's next fire time is still computed and scheduled — a failed fire does not break the schedule.
- **Job handler timeout:** Inline dispatch respects the cancellation token. If the user's delegate does not honor cancellation, the scheduler does not wait for it — the fire is considered dispatched the moment the delegate is invoked.
- **Registration race:** Concurrent `RegisterAsync("same-name", ...)` calls: exactly one succeeds; all others throw `DuplicateJobNameException`. Enforced via the registry command channel (single-writer semantics).
- **Store failure on save:** `IScheduleStore.SaveAsync` throwing during registration: the registration is rolled back and the exception is propagated to the caller. The in-memory state stays consistent with the store.
- **Store failure on checkpoint:** `IScheduleStore.RecordFiredAsync` throwing after dispatch: the failure is logged and metric-tagged, but the next fire is still computed and scheduled. On next restart, the missed-fire policy handles the gap (this is the contract: checkpoints are best-effort, policies close the loop).
- **Graceful shutdown:** On `IHostedService.StopAsync`, the tick loop stops accepting new fires, waits up to the configured shutdown timeout for in-flight dispatches to complete, and then returns. In-flight dispatches that do not honor cancellation are abandoned — documentation states this contract bluntly (cooperative cancellation is routinely oversold; Hangfire#2452/#1298). Additionally (R9): shutdown may arrive at any await point between due-detection and dispatch handoff, and the job must remain schedulable — never left in a permanently dead state (quartznet#2804). `StopAsync` is idempotent, tolerates stop-before-fully-started, and never throws from disposed primitives under start/stop hammering (NCronJob#172). No dispatch callback runs after the host's service provider is disposed (quartznet#1781/#1740).
- **Empty registry:** A scheduler with no jobs starts normally and the tick loop idles indefinitely (no busy wait). Registering the first job wakes the loop via the command channel.

### DR-11: Performance targets (revised per R2)

Discovery reframing: **no incumbent competes on allocations** — the competitive axes are the
orchestrator integration, cadences, missed-fire policies, the harness, and AOT (see Competitive
Landscape). Performance remains a design constraint (the engine must never be the bottleneck), but
allocation targets are **benchmark-tracked, not merge-gated**; only the correctness-flavored targets
gate.

**Acceptance criteria (correctness — merge-gating):**
- Saturation correctness: 1,000+ jobs due at the same instant are all dispatched, none lost (see DR-10).
- Fire latency (time between `NextFireAt` and dispatch handoff), p99 at 10K registered jobs, in-memory store: < 5ms.
- Per-fire cost is O(log n) in registered-job count (heap property — verified by benchmark scaling curve).

**Benchmark targets (tracked, regressions investigated, not build-failing):**
- Dispatch handoff steady-state allocations: approaching 0 B per fire. Note (R2): the illustrative
  tick loop in this document allocates per wait cycle (linked CTS, `Task.Delay`, `WhenAny` tasks);
  a truly zero-alloc loop requires a reusable wake primitive and pooled dispatch state, and may be
  deferred to a follow-up optimization pass without changing the public API.
- Register a new job: < 256 B (one `JobRecord` + heap insertion).
- Benchmarks added to `Bifrost.Benchmarks` under `Scheduling/` directory:
  - `RegistryRegistrationBenchmarks` — register, unregister, pause, resume
  - `TickEngineBenchmarks` — fire latency and allocation under load (Params: `JobCount = [10, 100, 1000, 10000]`)
  - `CadenceComputeBenchmarks` — `ComputeNextFire` for interval, cron, jittered
  - `DispatchBenchmarks` — orchestrator vs inline vs custom dispatch overhead

### DR-12: Delivery semantics — at-least-once per occurrence (new, R3)

The scheduler's consumer contract is **at-least-once execution per scheduled occurrence**, stated
bluntly in documentation. This matches the universal industry posture: Quartz recovery ("some of
the job's 'work' will be executed twice"), Hangfire ("your job will be performed at least once…
Try to do all your background job methods idempotent"), Sidekiq, Temporal activities, AWS
EventBridge Scheduler, and GCP Cloud Scheduler all document at-least-once and require consumer
idempotency.

**Acceptance criteria:**
- The duplicate window is documented precisely: a crash between dispatch handoff and the
  `RecordFiredAsync` checkpoint means the missed-fire policy re-fires the occurrence on restart
  (with a durable store). `Coalesce` (the default) bounds duplicates to one catch-up fire.
- `JobFireContext.FireTime` is the **scheduled occurrence time** — stable across a re-fire of the
  same occurrence — never the wall-clock dispatch time. `(JobName, FireTime)` is the documented
  idempotency/dedup key (GCP Cloud Scheduler precedent: job name + schedule-time header).
- Future durable stores must be able to unique-constrain `(JobName, ScheduledFireTime)` per
  occurrence (the Hangfire#1852 class of multi-node duplicate).
- XML docs on `IJobDispatcher` and inline dispatch state the idempotency expectation on consumers,
  using the blunt phrasing precedents above.

### DR-13: AOT compatibility — end to end (new, R8)

Native AOT compatibility is a named, headline requirement — and the precise claim matters: TickerQ
already declares `IsAotCompatible=true` on its core packages and ships a `PublishAot=true` sample,
so "first/only AOT scheduler" is **not defensible**. The defensible claim this design commits to:
**every shipped scheduling package — including the persistence path — declares and validates AOT
compatibility.**

**Acceptance criteria:**
- All shipping `Bifrost.Scheduling*` packages set `IsAotCompatible=true` (already the repo
  standard) with zero trim/AOT analyzer warnings under warnings-as-errors.
- CI runs a `PublishAot` smoke test of a sample app exercising registration, all three dispatch
  modes, and the in-memory store.
- No reflection-based job activation, no `Type.GetType` from persisted strings, no expression-tree
  compilation anywhere in the scheduling packages. Custom dispatchers (`DispatchVia<TDispatcher>`)
  resolve via generic DI registration — `JobRecord.DispatcherTypeName` is diagnostic metadata only,
  never an activation input.
- Dependency audit: Cronos is AOT-safe by construction (the spike's source audit found zero
  reflection/serialization across its 12 source files); it cannot declare `IsAotCompatible` (no
  net8+ TFM), so Bifrost's own trim-analyzer CI run is the proof.
- Binding constraint on the future Marten/Postgres adapter: `JobRecord`/`Cadence` serialization must
  use source-generated contracts (no reflection-based JSON), or the end-to-end claim dies. Recorded
  now because it shapes the `JobRecord` type: string-keyed metadata, no `Type` references,
  polymorphic `Cadence` serializable via discriminator.
- README/package descriptions use only the defensible comparative phrasing — "no reflection-based
  job activation, no type-name serialization, no expression-tree compilation" — never "first" or
  "only."

## Technical Design

### Package layout

The scheduler is isolated into a new package so the core `Bifrost` package remains unaffected for consumers who don't need scheduling.

```
src/
├── Bifrost.Scheduling.Core/       # Contracts: IScheduleStore, IScheduleRegistry,
│   │                              # Cadence, JobRecord, IJobDispatcher,
│   │                              # events, metrics names
│   └── Bifrost.Scheduling.Core.csproj
│
├── Bifrost.Scheduling/            # Tick engine, InMemoryScheduleStore,
│   │                              # fluent builder, DI extensions, health check,
│   │                              # test harness. Depends on Bifrost.Scheduling.Core
│   │                              # and Bifrost (for IWorkOrchestrator dispatch).
│   └── Bifrost.Scheduling.csproj
│
└── Bifrost.Scheduling.Marten/     # Deferred to follow-up release.
                                    # IScheduleStore on Marten document session +
                                    # the multi-instance coordination contract,
                                    # which is DESIGNED WITH this adapter (DR-6/R4).
                                    # Serialization: source-generated only (DR-13).
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

    // R1: no clock access in a static factory — the registry resolves the delay against its
    // injected TimeProvider at registration time (DR-7 banned-API rule).
    public static Cadence After(TimeSpan delay) => new RelativeOneShotCadence(delay);
}

public sealed record IntervalCadence(TimeSpan Interval, double Jitter = 0) : Cadence { ... }
public sealed record CronCadence(string Expression, TimeZoneInfo? TimeZone) : Cadence { ... }
public sealed record OneShotCadence(DateTimeOffset FireAt) : Cadence { ... }
public sealed record RelativeOneShotCadence(TimeSpan Delay) : Cadence { ... } // resolved to OneShotCadence by the registry at registration time

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
    string? DispatcherTypeName,    // diagnostic metadata ONLY — never an activation input (DR-13)
    IReadOnlyDictionary<string, string> Metadata);

public interface IScheduleStore
{
    ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct);
    ValueTask SaveAsync(JobRecord job, CancellationToken ct);
    ValueTask RecordFiredAsync(string jobName, DateTimeOffset firedAt, DateTimeOffset? nextFireAt, CancellationToken ct);
    ValueTask DeleteAsync(string jobName, CancellationToken ct);
}

// Coordination contract intentionally ABSENT from v1 (DR-6 / R4).
// IExclusiveScheduleStore + the leadership lease are designed together with the first
// durable adapter (Bifrost.Scheduling.Marten), where the real store can inform the shape:
// epoch/fencing token, LastRenewedAt/ExpiresAt, conditional checkpoint writes.

public interface IJobDispatcher
{
    ValueTask DispatchAsync(JobFireContext context, CancellationToken ct);
}

public readonly record struct JobFireContext(
    string JobName,
    DateTimeOffset FireTime,    // the SCHEDULED occurrence time — stable across a re-fire of the
                                // same occurrence; (JobName, FireTime) is the idempotency key (DR-12)
    DateTimeOffset? NextFireAt,
    IServiceProvider Services);
```

### Tick loop (pseudo-code)

> Illustrative control flow only. Not allocation-faithful (the linked-CTS/`Task.WhenAny` wait shown
> here allocates per cycle — see DR-11/R2), and the DR-7 early-wake clamp is elided for brevity.

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
            // Compute from the SCHEDULED occurrence time (nextFire), not the wall clock —
            // DR-7 early-wake clamp / DR-12 stable FireTime.
            var nextNext = handle.Cadence.ComputeNextFire(lastFiredAt: nextFire, now);
            _ = _router.DispatchAsync(handle, nextFire);   // fire-and-forget to pool
            _ = _store.RecordFiredAsync(handle.Name, nextFire, nextNext, default);

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
    // Tune the tick-loop SchedulerOptions (fault-recovery thresholds + backoff,
    // shutdown grace). Composes across calls; validated at AddScheduler (e.g. the
    // RestartBackoff * MaxRestartsInWindow < RestartWindow constraint).
    ISchedulerBuilder ConfigureOptions(Action<SchedulerOptions> configure);
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
  - The mined edge-case inventory (`docs/research/2026-06-12-durable-scheduling-precedents.md` §6) is the authoritative DR-10 checklist (R9): DST fire-count + monotonic-progress property tests, early-wake clamp, per-job failure isolation, shutdown injection at every await point, idempotent `StopAsync` under start/stop hammering, saturation correctness, resume-fires-nothing pin.
- **Architecture/banned-API test** (R1/DR-7): shipping scheduling code contains no `DateTime*.Now/UtcNow` and no non-`TimeProvider` `Task.Delay` — analyzer or arch-test, build-failing.
- **AOT validation** (DR-13): `PublishAot` smoke app in CI + trim-warnings-as-errors on all scheduling packages.
- **Integration tests** covering the common path: scheduler + `IWorkOrchestrator<TWork>` + resilience + DLQ. Scheduled job fails N times → dead-letters → scheduler keeps firing.
- **Benchmarks** in `Bifrost.Benchmarks/Scheduling/` validating DR-11 targets. CI runs these in `--job Dry` smoke mode; full runs are manual / release-gate.
- **Coverage gate** — 80% maintained across line, branch, and method for `Bifrost.Scheduling.Core` and `Bifrost.Scheduling`, matching the existing Bifrost repo standard.

## Exploration Summary

Three tick-engine approaches were considered during Phase 2:

- **Approach A — Min-heap priority queue, single loop (selected).** Single background loop, min-heap keyed by `NextFireAt`, mutations via command channel. O(log n), allocation-light steady state. The test story is solved by `TimeProvider` injection + `FakeTimeProvider`. (Earlier drafts mislabeled this a "timer wheel" — a real Varghese & Lauck wheel is O(1) and pays off at ~100K+ timers; the heap is correct at this scale. R5.)
- **Approach B — Scan-based tick loop.** Periodic scan of a job dictionary. O(n) per tick, simple to test, but concedes the high-volume differentiation the issue explicitly calls out as a Wolverine gap.
- **Approach C — Per-job `PeriodicTimer`.** Trivial code, but inconsistent checkpointing and bad at scale (1K+ timers). Loses the single coherent store-hook point that makes batched checkpointing viable. Rejected.

Four architectural dimensions were settled in Phase 1 and are not re-explored here: layered scope (design full / ship MVP), fluent + runtime registry, hybrid pluggable dispatch, and pluggable storage with single-leader default + opt-in exclusive store. See the ideate session transcript for the trade-offs on each.

## Question Resolutions (updated 2026-06-12)

1. **Cron library: Cronos.** Committed in the plan; re-confirmed by the spike's AOT audit (zero
   reflection/serialization across its source; battle-tested as the engine inside NCronJob and
   Hangfire — TickerQ uses NCrontab instead).
2. **Package naming: `Bifrost.Scheduling`.** Committed in the plan.
3. **`FakeTimeProvider` dependency: separate `Bifrost.Scheduling.Testing` package.** Committed in
   the plan — core stays free of test-provider dependencies.
4. **Missed-fire policy on `Paused` jobs: `Pause` is intentional absence.** Resume does **not**
   trigger missed-fire policy; policies apply only on startup recovery from a store. Committed in
   the plan, validated by the spike (Quartz's resume-applies-misfire default is a recurring user
   surprise), pinned by test in DR-10.
5. **Multi-instance rollout: deferred entirely — including the contract** (R4 / DR-6). The
   coordination abstraction is designed together with the first durable adapter, not before it. The
   `basileus#145` forcing use case is single-instance today.

**Still open:**

6. **Relationship to future priority queues (0.5.0).** The existing roadmap proposes priority queues on `IWorkOrchestrator`. Do scheduled jobs interact with priority queues — e.g., can `.DispatchTo<IPriorityWorkOrchestrator<T>>(priority: 3)` declare a scheduler-controlled priority? Likely yes, but out of scope for the scheduler v1 design.
