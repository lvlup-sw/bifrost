# Discovery: Scheduling API Precedents & Modern Research

**Feature ID:** `durable-scheduling-precedents` (discovery workflow)
**Feeds:** design revision of `docs/designs/2026-04-10-durable-scheduling-api.md` ([issue #16](https://github.com/lvlup-sw/bifrost/issues/16))
**Date:** 2026-06-12
**Method:** four parallel research agents — (1) feature/ergonomics matrix of modern .NET schedulers, (2) issue-tracker mining for battle-tested edge cases, (3) Native AOT landscape verification, (4) delivery-semantics / fencing / naming precedents.

---

## Executive summary

1. **The competitive landscape moved.** NCronJob (active, MIT, TimeProvider-based) already ships ~60-70% of the proposed registry surface. TickerQ (very active, source-generator-based, persistent, dashboard, multi-node) brackets the proposal from the durable side. The design's Wolverine-only gap analysis is stale.
2. **The defensible differentiators are:** orchestrator dispatch (scheduling as a trigger source for Bifrost's resilience/DLQ/autoscaling pipeline), interval + jittered cadences (inexpressible in cron; nobody ships jitter), declarative **per-job** missed-fire policies (only Quartz/Hangfire have them, with baggage), the deterministic test harness (nobody ships `AdvanceAsync`-style sync semantics), and **end-to-end AOT including the persistence path** (TickerQ claims AOT on core only; its stores are EF Core/Redis, respectively experimental and broken under trimming).
3. **"First/only AOT scheduler" is NOT a defensible claim** — TickerQ declares `IsAotCompatible=true` on its core packages and ships a `PublishAot=true` sample. The defensible phrasing is narrower (see [AOT landscape](#3-aot-landscape)).
4. **At-least-once is the industry-standard consumer contract** — Hangfire, Sidekiq, Temporal activities, EventBridge Scheduler, and Cloud Scheduler all document at-least-once + consumer idempotency. Checkpoint-after-dispatch + `Coalesce` re-fire is squarely inside the norm; the design just needs to *say it* and name the dedup key.
5. **The fencing-less `ILeadershipLease` should not ship in v1.** Kleppmann's argument makes `bool IsHeld` a local-knowledge anti-pattern; `IDistributedCache` is the canonical .NET cautionary tale for freezing a minimal interface before its first real implementation. Defer the contract; when it ships, it needs an epoch/fencing token and store-side conditional-write requirements (or be an abstract class, per the HybridCache precedent).
6. **"Durable" is a naming collision.** In 2026 it denotes the durable-*execution* category (Temporal, Restate, Dapr Workflow, AWS Lambda Durable Functions) and collides head-on with Microsoft's managed product literally named **"Durable Task Scheduler."** v1 (in-memory only) is also literally not durable. Rename the positioning.
7. **42 battle-tested edge cases** mined from Quartz.NET, Hangfire, Coravel, TickerQ, and NCronJob trackers, generalized into test cases for the DR-10 matrix — concentrated in DST transitions, misfire-policy semantics, duplicate fires, shutdown races, and clock anomalies.

---

## 1. Precedent matrix

| Dimension | NCronJob | TickerQ | Coravel | Quartz.NET | Hangfire | Bifrost (proposed) |
|---|---|---|---|---|---|---|
| **Cadences** | Cron only for recurring (Cronos, TZ param); instant jobs now/after/at; no interval primitive, no jitter | CronTicker (cron) + TimeTicker (one-shot absolute); no interval/jitter; TZ undocumented | Rich intervals (`EverySecond()`→`Monthly()`), `Cron()`, `Zoned(TZ)`, `Once()`; no jitter | SimpleTrigger (interval), CronTrigger (TZ), CalendarInterval, RFC 5545 RRULE; no jitter | Cron recurring (TZ), delayed, continuations; ~1 min granularity; no jitter | Interval, cron+TZ (Cronos), one-shot at/after, **jittered interval** |
| **Runtime control** | `IRuntimeJobRegistry`: register/remove/update/enable/disable + trigger-now; thin inspection | Add/Update/Delete via managers; `IsEnabled` pause; dashboard trigger/cancel | None — startup-only config | Full: schedule/unschedule/pause/resume/trigger at job/trigger/group level | AddOrUpdate/Remove/Trigger; **no first-class pause** | Register/Pause/Resume/Trigger/Unregister + GetJobs, fluent DI + runtime |
| **Missed-fire policy** | None (nothing persists, no catch-up concept) | Catch-up on restart; global config only, not per-job | In-process lag replayed; app-down = lost | Per-trigger misfire instructions (SmartPolicy default; threshold-based) | Per-job `MisfireHandlingMode` (Relaxed/Strict/Ignorable, 1.8+) | **Declarative per-job**: Coalesce / FireAllMissed / SkipMissed |
| **Execution model** | DI-scoped jobs inline; Polly retries; concurrency control; dependency chains | Source-generated `[TickerFunction]`; own worker host; per-function concurrency | `IInvocable` from DI; `PreventOverlapping()`; in-memory queue | Own pool; IJob via DI factory | Workers poll storage; expression-tree-serialized calls; at-least-once | Min-heap loop → pluggable dispatch: **IWorkOrchestrator** (resilience/DLQ/autoscaling), inline, custom |
| **Persistence** | None, by design | In-memory, EF Core (PG/SQLServer/SQLite/MySQL), Redis | None (Pro is commercial) | RAMJobStore or ADO.NET store | Required: SQL Server, Redis (Pro), community stores | Pluggable `IScheduleStore`; v1 in-memory; Marten/PG deferred |
| **Multi-instance** | None | EF row-lock w/ OwnerNode; Redis heartbeats | None native | DB-backed clustering (lock table, failover) | Native via shared storage + distributed locks | None in v1 (single-node) |
| **Observability** | Notification/progress hooks; no OTel/health/dashboard | SignalR dashboard; OTel package; no health checks | `OnError`; dashboard paid | OTel contrib (beta, traces); community dashboards | Built-in dashboard; OTel contrib beta; community health checks | First-party OTel metrics + event stream + health checks |
| **Test-time control** | TimeProvider internal (swap possible, no harness/sync) | None documented | None (real-time loop) | 4.x (unreleased): `UseTimeProvider<FakeTimeProvider>()`, no harness semantics | None | **First-class harness**: AdvanceAsync / FireDueJobsAsync / WaitForIdleAsync |
| **Native AOT** | No claim, no flags, real blockers (`Expression.Compile` in dynamic jobs) | `IsAotCompatible=true` on core pkgs; "zero reflection"; persistence/dashboard pkgs NOT declared; open AOT bugs (#830, #863) | No claim; reflection dispatcher | No; reflection job activation, `Type.GetType` from stored strings | Architecturally AOT-hostile (expression-tree serialization) | `IsAotCompatible=true` on **all** packages |
| **Health (mid-2026)** | v4.10.2 Jun 2026; MIT; active (Giesel et al.) | v10.3 Apr 2026; MIT/Apache-2.0; 3.5k★; very active; tracks .NET majors | v6.0.2 Jan 2025 (~17 mo stale); MIT | v3.18.1 Apr 2026; Apache-2.0; 4.x in development (TimeProvider, net8+) | v1.8.23 Feb 2026; LGPL+commercial; incumbent, slow-moving | n/a |

Also noted: **FluentScheduler** v6 shipped Dec 2025 (not dead, but feature-thin); **Sundial** (Chinese .NET ecosystem, invisible to Western NuGet audience); Wolverine/MassTransit scheduled messages as prior art for "scheduler feeds an execution pipeline."

### Strategic verdict: "NCronJob + 50 lines"?

Honest answer: **NCronJob covers roughly 60-70% of the proposed registry surface**, and the "50 lines of glue" is true for exactly one feature — dispatch (an NCronJob job body calling `orchestrator.EnqueueAsync` inherits Bifrost's execution pipeline in ~20 lines). If the pitch were "fluent cron registration with runtime control," that ships today, MIT-licensed and actively maintained. TickerQ separately covers what NCronJob refuses to (persistence, multi-node, dashboard, AOT-on-core).

The genuinely differentiated remainder, none of which is 50 lines:

1. **Cadence primitives** — NCronJob is cron-expression-only; `Every(90.Seconds())` and jittered intervals are inexpressible in cron. *Nobody* in the field ships jitter.
2. **Declarative per-job missed-fire policies** — NCronJob has no misfire concept; TickerQ's catch-up is global config; only Quartz (per-trigger instructions + threshold baggage) and Hangfire (`MisfireHandlingMode`) have per-job policy. A clean `Coalesce`/`FireAllMissed`/`SkipMissed` enum is a real gap in the lightweight tier. (Note: its value is mostly latent until a durable store exists — in-memory misfires occur only on in-process stalls and paused jobs.)
3. **The deterministic test harness** — nothing in the field ships `AdvanceAsync`/`FireDueJobsAsync`/`WaitForIdleAsync` synchronization semantics. Quartz 4 (unreleased) gets TimeProvider *injection* but no idle-sync. This is the most defensible differentiator because it is an architectural property, not a bolt-on.
4. **AOT end-to-end** — see §3.

**Positioning consequence:** Bifrost scheduling is not justified as "a better NCronJob." It is justified as *scheduling as a trigger source for an orchestration pipeline you already run*, plus the four differentiators. The design doc should say this explicitly, including honest "when NOT to use" guidance: teams not using `IWorkOrchestrator` should be pointed at NCronJob (simple, in-memory) or TickerQ (durable, dashboard).

---

## 2. Delivery semantics

### What the field documents

- **Quartz.NET clustering** coordinates via a DB row lock (`SELECT … FOR UPDATE` on a `LOCKS` table) — lock and trigger-state transition commit in the *same transaction against the same DB*, so no fencing token is needed. Guarantee is per-firing exclusivity under normal operation; crash recovery is **opt-in at-least-once** (`RequestsRecovery=true`), and the docs say plainly: *"In-progress Jobs marked 'recoverable' are automatically re-executed after a scheduler fails. This means some of the job's 'work' will be executed twice."* Clock sync across nodes is a hard documented precondition (within 1 second).
- **Hangfire**: *"Hangfire takes the responsibility to process it with the at least once semantics"*; *"as a general rule remember, that your job will be performed at least once… Try to do all your background job methods idempotent."* Its own throttling docs concede `DisableConcurrentExecution` "may reduce the probability of violation of this safety property, but the only way to guarantee it is to use transactions or CAS-based operations… to make them idempotent."
- **Sidekiq**: *"Sidekiq will execute your job at least once, not exactly once… Sidekiq makes no exactly-once guarantee at all."*
- **Temporal**: activity *completion* is recorded exactly once, but *"the Activity may be executed multiple times."*
- **AWS EventBridge Scheduler**: *"at-least-once event delivery to targets."* **GCP Cloud Scheduler**: at-least-once, no exactly-once option; recommends dedup via job name + the schedule-time header (constant across retries).

### Conclusion for Bifrost

Checkpoint-after-dispatch-handoff + `Coalesce` re-fire after crash is **squarely the industry norm**. The gap is purely documentary. The design should:

1. State **at-least-once per scheduled occurrence** as a named requirement (the duplicate window: crash between dispatch handoff and `RecordFiredAsync` checkpoint).
2. Require consumer idempotency in the consumer contract, using the field's blunt phrasing precedents above.
3. Name the dedup key: `JobFireContext` already carries `(JobName, FireTime)` — document the pair as the idempotency key, GCP-style. `FireTime` must be the *scheduled* occurrence time (stable across a re-fire), not the wall-clock dispatch time.

---

## 3. AOT landscape

Verified per-library status (source-level audit, mid-2026):

| Library | `IsAotCompatible`? | Reality |
|---|---|---|
| Quartz.NET | No | `Activator.CreateInstance` job activation, `Type.GetType(name)` from persisted strings, reflective property setting; trim-analyzer line commented out; zero AOT issues in tracker |
| Hangfire | No (no modern TFM) | Architecturally AOT-hostile: expression-tree capture + JSON-serialized invocation data + `MethodInfo.Invoke`; issue #2478 (Native AOT) open since Dec 2024, zero maintainer response |
| Coravel | No | `ActivatorUtilities.CreateInstance(sp, Type)` invocable activation; string-based reflective event dispatch |
| NCronJob | No (zero AOT/trim mentions in repo) | `Expression.Lambda(...).Compile()` in dynamic-job and condition paths; typed path plausibly trim-safe but unannotated, untested |
| **TickerQ** | **Yes — core packages only** | Genuine source-generator discovery; first-party `PublishAot=true` sample. But: persistence (EF Core — officially *experimental* under AOT per Microsoft) and Redis (open trimming bug #830) packages are NOT declared; AOT hardening landed Feb–May 2026 and is still in flight |
| FluentScheduler | No | Probably AOT-clean by accident (pure delegates); undeclared, untested, feature-thin |
| **Cronos** | N/A (no net8+ TFM) | **AOT-safe by construction**: full source audit found zero reflection/serialization across all 12 files. Safe dependency; Bifrost's own trim-analyzer CI provides the proof Cronos can't declare |

**Ecosystem trend:** AOT compatibility is now a first-class selection criterion for server-side .NET libraries (ASP.NET Core AOT since .NET 8, official library-author guidance, Aspire-era cold-start pressure). The notable laggard is EF Core itself — which is exactly the durable-storage layer of the one scheduler claiming AOT.

**Verdict on the headline claim:** "first/only AOT-compatible scheduler" is **not defensible** — TickerQ beat us to it and any reviewer will find it in five minutes. Defensible phrasings:

1. *"AOT-compatible scheduling end to end — including the persistence path"* — the genuine open gap, **provided the future Marten/Postgres adapter clears the bar** (this becomes a hard design constraint on adapter serialization: no reflection-based JSON, source-generated contracts only).
2. *"Every shipped package declares `IsAotCompatible` and is validated with `PublishAot` + trim-warnings-as-errors in CI"* — a proof-level claim TickerQ can't currently make (2 of 6 packages, open AOT bugs).
3. Comparative and safe: *"no reflection-based job activation, no type-name serialization, no expression-tree compilation."*

AOT should be promoted from an unstated repo convention to a **named design requirement** with the CI validation as acceptance criteria.

---

## 4. Leader election & fencing

### The failure mode (Kleppmann)

Client 1 acquires lease → stop-the-world GC → lease expires → client 2 acquires → client 1 resumes, still believing it holds the lease. The deeper point: **a client can never locally know its lease is valid** — any `bool IsHeld` is stale the instant it returns. The fix is a *fencing token* (strictly monotonic acquisition counter) checked **by the resource**, not the client. Kleppmann's split: efficiency locks (duplicate work = wasted cost) may skip fencing; correctness locks must not.

### How real systems handle it

| System | Fencing? | Mechanism |
|---|---|---|
| Quartz clustering | No token — doesn't need one | Lock + state transition commit in the same DB transaction; the database serializes; a stale node's write conflicts at commit |
| Kubernetes Lease / client-go | Explicitly disclaims fencing (*"does not guarantee that only one client is acting as a leader"*) | `holderIdentity` + `leaseTransitions` (weak epoch); positioned as efficiency-only; controllers must be idempotent |
| Hangfire | No | Best-effort locks; correctness pushed onto consumer idempotency/CAS |
| Postgres advisory locks | No | Advisory by name and design; lifetime tied to session/connection |
| **Wolverine/Marten** (the Critter Stack precedent directly relevant to the planned adapter) | No token | Simplified Bully algorithm + heartbeats + advisory lock; correctness backstopped by the transactional inbox/outbox, not the election |

### The .NET interface-evolution cautionary tale

`IDistributedCache`: shipped minimal in 2016, widely implemented externally, then officially described in the .NET 9 HybridCache epic (dotnet/aspnetcore#53255) as *"not particularly developed… lacks many desirable features"* — and was **superseded rather than amended**, because adding members to a shipped interface breaks every external implementor. HybridCache is an abstract class for exactly this reason.

### Conclusion for Bifrost

Given (a) the scheduler's contract is already at-least-once, (b) the real stores will be transactional DBs where Quartz-style same-transaction enforcement works, and (c) no implementation ships in v1:

1. **Defer `IExclusiveScheduleStore` and `ILeadershipLease` from the v1 public surface entirely.** An unimplemented coordination contract is a guess, and this one already exhibits the two classic flaws (`bool IsHeld` local-knowledge anti-pattern; no epoch → unaddable later without breaking).
2. When the Marten adapter forces the contract into existence, it must include: an **epoch/fencing token** (`long Epoch`, even if a constant for single-node stores), **`LastRenewedAt`/`ExpiresAt`** exposure instead of a bare `IsHeld` bool, and a documented requirement that **checkpoint writes are conditional on ownership** (`UPDATE … WHERE owner = @me AND epoch = @epoch`, or same-transaction lock validation, Quartz-style). The lease is then honestly advisory (efficiency), with correctness enforced store-side — matching the Wolverine/k8s posture and the at-least-once contract.
3. Prefer an **abstract class** over an interface for the lease type when it ships (HybridCache precedent), preserving non-breaking evolution.

---

## 5. "Durable" naming

"Durable execution" is now an established **category term**, not an adjective: Temporal ("crash-proof execution" via journaled replay), Restate, Dapr Workflow, DBOS, Inngest, Cloudflare Workflows. Microsoft has formalized it — *"Durable execution is an industry-wide approach to making ordinary code fault-tolerant by automatically persisting its progress"* — and ships a GA managed service literally named **"Durable Task Scheduler"** (2025). AWS joined with **Lambda Durable Functions** (Dec 2025). Quartz adds a third adjacent meaning: a "durable job" there is merely one that survives having no triggers.

Naming this feature "durable scheduling" therefore invites: (1) category confusion — readers will expect journaled, replayable workflow code, which this is not (it persists *schedule/fire state*, not execution state); (2) a head-on collision with an Azure product name in the same ecosystem; (3) an accuracy problem — v1 ships in-memory-only persistence, so v1 is precisely *not* durable.

**Recommendation:** drop "durable" from the feature/package positioning. The packages are already well-named (`Bifrost.Scheduling*`). Position as **"Bifrost Scheduling — pluggable schedule persistence with at-least-once recovery"**; reserve "durable" language for the future store-backed mode if at all. Alternative phrases with precedent: "persistent scheduling," "recoverable scheduling" (aligns with Quartz's `RequestsRecovery` vocabulary).

---

## 6. Edge-case inventory → DR-10 test matrix

42 findings mined from public trackers, generalized into test cases. The two highest-leverage families across all five trackers: **atomic claim under multi-worker stress** (TickerQ#33, Hangfire#1960, quartznet#1758) and **DST fall-back per-minute fire counting** (quartznet#2475, Hangfire#567).

### 6.1 Missed-fire / misfire

| Source | Lesson → test case |
|---|---|
| [quartznet#1545](https://github.com/quartznet/quartznet/issues/1545) | Rescheduling an already-run job preserved historic `StartTimeUtc` → spurious immediate fire (weekly emails every ~29h). **Test:** rescheduling with an unchanged future schedule never fires before the next legitimate occurrence. |
| [quartznet#636](https://github.com/quartznet/quartznet/issues/636) | Past `StartAt` fires instantly even with past `EndAt`. **Test:** fully-past window = zero executions; partially-past follows declared policy, never silent fire-now. |
| [quartznet#3096](https://github.com/quartznet/quartznet/issues/3096) | Regression silently changed one misfire policy's semantics. **Test:** parameterized matrix pinning *every* policy's exact catch-up count. |
| [quartznet#1109](https://github.com/quartznet/quartznet/issues/1109) | Misfire *threshold* creates two behavioral regimes ("late" vs "misfired"). **Test:** downtime below/above any threshold boundary is an explicit test axis. Bifrost has no threshold — keep it that way; policies apply uniformly. |
| [Hangfire#546](https://github.com/HangfireIO/Hangfire/issues/546) | Yearly job's only occurrence during downtime silently skipped. **Test:** N missed occurrences → observable per policy: skip-all / fire-once / fire-each. |
| [Hangfire#751](https://github.com/HangfireIO/Hangfire/issues/751) | 679 recurring jobs: some never fire — per-tick throughput can't enumerate all due jobs. **Test:** 1000+ jobs due at the same instant → every one dispatches (or carries to next tick), none silently lost. |
| [TickerQ#83](https://github.com/Arcenox-co/TickerQ/issues/83) | Restart scheduled next occurrence for *tomorrow* instead of later today (UTC "today" boundary). **Test:** restart at T with same-day occurrence due → fires same-day, regardless of host/DB timezone. |

### 6.2 DST transitions

| Source | Lesson → test case |
|---|---|
| [quartznet#2475](https://github.com/quartznet/quartznet/issues/2475) | Per-minute cron fired non-stop for the entire repeated hour after fall-back (still unfixed). **Test:** exactly 60 (or 120, per declared policy) fires in the repeated hour — never a tight refire loop. |
| [quartznet#2497](https://github.com/quartznet/quartznet/issues/2497), [#332](https://github.com/quartznet/quartznet/issues/332), [#2349](https://github.com/quartznet/quartznet/issues/2349) | Schedules anchored in the skipped spring-forward hour → same-instant loop / infinite fire loop. **Test:** monotonic-progress property test — `ComputeNextFire(t)` strictly > `t` across the whole DST week, both boundaries, multiple zones incl. half-hour offsets. |
| [quartznet#539](https://github.com/quartznet/quartznet/issues/539) | Enumerating future fires across DST silently drops dates. **Test:** N occurrences across boundary = N distinct ordered instants. |
| [Hangfire#567](https://github.com/HangfireIO/Hangfire/issues/567) | Per-minute jobs silent for exactly one hour after BST fall-back. **Test:** no 60-minute silence window in the repeated hour. |
| [Hangfire#530](https://github.com/HangfireIO/Hangfire/issues/530)/[#529](https://github.com/HangfireIO/Hangfire/issues/529)/[#537](https://github.com/HangfireIO/Hangfire/issues/537) | Invalid-local-time conversion **crash-looped the shared scheduler loop, killing ALL jobs**. **Test:** (a) nonexistent local times map per policy without throwing; (b) one job's schedule-computation exception never halts other jobs' scheduling. |

### 6.3 Clock anomalies

| Source | Lesson → test case |
|---|---|
| [quartznet#1508](https://github.com/quartznet/quartznet/issues/1508)/[#2034](https://github.com/quartznet/quartznet/issues/2034) | Backward clock jump silently stopped ALL firing until restart (slept on absolute wall-clock deadline). **Test:** clock jumps backward by days → scheduler re-arms within one wait cycle. Validates the DR-10 clock-skew requirement; the command-channel wake design must also wake on time anomalies. |
| [NCronJob#327](https://github.com/NCronJob-Dev/NCronJob/issues/327) | **`Task.Delay` wakes ~16ms early on Windows** → job ran early, then next-occurrence computed from the early timestamp → duplicate fire. **Test:** timer firing up to 20ms early → exactly one execution per occurrence (clamp/re-sleep; never compute next fire from a timestamp before the scheduled instant). Directly applicable to the min-heap loop. |
| [NCronJob#169](https://github.com/NCronJob-Dev/NCronJob/issues/169) | TimeProvider not used in *all* code paths. **Lesson:** arch-test (banned-API analyzer) that production code never calls `DateTime*.Now/UtcNow` — this would have caught the design's own `Cadence.After` bug (see R1). |
| [coravel#391](https://github.com/jamesmh/coravel/issues/391) | VM pause/sleep → catch-up storm replaying every missed tick, log flood. **Test:** 30-min stall → per-minute job fires per policy (Coalesce default: once), catch-up rate-limited and quiet. |
| [Hangfire#2330](https://github.com/HangfireIO/Hangfire/issues/2330) | Heartbeat liveness compared timestamps from two different clocks (app vs DB). **Lesson for future exclusive store:** lease-expiry math must use a single authoritative clock. |
| [quartznet#703](https://github.com/quartznet/quartznet/issues/703) | Cluster nodes with skewed clocks ran non-concurrent jobs in parallel. **Lesson for future adapter:** coordination decisions must use the store's clock, not node clocks. |

### 6.4 Graceful shutdown

| Source | Lesson → test case |
|---|---|
| [quartznet#2804](https://github.com/quartznet/quartznet/issues/2804) | Shutdown between trigger-acquire and handoff marked trigger ERROR permanently. **Test:** inject shutdown at every await point between due-detection and dispatch handoff; job state must return to schedulable, never a dead state. |
| [Hangfire#2452](https://github.com/HangfireIO/Hangfire/issues/2452)/[#2378](https://github.com/HangfireIO/Hangfire/issues/2378) | Docs over-promised re-queue-on-dispose; reality depends on token observation. **Test:** in-flight job that does/doesn't observe cancellation × host disposal with timeout T → assert exact terminal state for each combination. Document the contract bluntly. |
| [coravel#221](https://github.com/jamesmh/coravel/issues/221) | `StopAsync` spin-waited on a flag that never flipped → service stuck "Stopping" forever. **Test:** shutdown completes within host timeout while a per-minute job is mid-flight. |
| [NCronJob#172](https://github.com/NCronJob-Dev/NCronJob/issues/172) | `ObjectDisposedException` from CTS race in StopAsync (flaky CI). **Test:** hammer start/stop cycles incl. stop-before-started; StopAsync idempotent, never throws on disposed primitives. |
| [quartznet#1781](https://github.com/quartznet/quartznet/issues/1781)/[#1740](https://github.com/quartznet/quartznet/issues/1740) | Scheduler outlived DI container → disposed-service crashes. **Test:** no dispatch callback runs after container disposal; in-flight scoped resolutions complete or cancel cleanly. |
| [Hangfire#1298](https://github.com/HangfireIO/Hangfire/issues/1298) | "Delete running job" only flips state; abort is cooperative. **Docs:** state plainly that non-cooperative job bodies cannot be stopped (matches DR-10's existing abandonment language). |

### 6.5 Duplicate fires

| Source | Lesson → test case |
|---|---|
| [quartznet#623](https://github.com/quartznet/quartznet/issues/623)/[#499](https://github.com/quartznet/quartznet/issues/499) | Overlapping app-pool recycles → multiple scheduler instances, multiplying fires. **Test:** second scheduler instance against same store joins or throws — never silently double-fires. (v1: second `AddScheduler` / double host start must be detected.) |
| [Hangfire#1960](https://github.com/HangfireIO/Hangfire/issues/1960)/[#90](https://github.com/HangfireIO/Hangfire/issues/90)/[#936](https://github.com/HangfireIO/Hangfire/issues/936) | Lease expired mid-job → second worker picked it up. **Lesson for future adapter:** lease renewal heartbeat; test both directions (alive 2× lease → no second fetch; kill -9 → IS re-fetched). |
| [TickerQ#33](https://github.com/Arcenox-co/TickerQ/issues/33) | EF read-modify-write claim = TOCTOU race → 3-4 duplicate executions with 5 workers. **Lesson:** claims must be atomic compare-and-set (`UPDATE … WHERE state='due'` affecting 1 row), stress-tested. |
| [TickerQ#458](https://github.com/Arcenox-co/TickerQ/issues/458) | Global `MaxConcurrency=1` ≠ per-job serialization; fix then caused stale-occurrence backlog. **Test:** if per-job non-concurrency is offered, define pile-up policy (queue/skip/cap) explicitly and assert it. |
| [TickerQ#187](https://github.com/Arcenox-co/TickerQ/issues/187) (+3 dupes) | N nodes cold-starting raced to seed occurrence rows → crash on unique index. **Lesson for adapter:** seeding must upsert. |
| [Hangfire#1852](https://github.com/HangfireIO/Hangfire/issues/1852) | Recurring job executed on all servers near-simultaneously. **Lesson:** per-occurrence dedup — occurrence identity `(JobName, ScheduledFireTime)` unique-constrained in durable stores. |
| [quartznet#2697](https://github.com/quartznet/quartznet/issues/2697) | Non-concurrency keyed per job-detail surprised users running same class under multiple keys. **Docs:** define identity = job *name*, not type. |

### 6.6 Pause/resume

| Source | Lesson → test case |
|---|---|
| Quartz docs + [#1109](https://github.com/quartznet/quartznet/issues/1109) | **Resume applies misfire policy → immediate fire on resume, recurring user surprise.** Validates the plan's committed decision (pause = intentional absence; resume does NOT trigger missed-fire). **Test:** pin it — paused N intervals → resume → zero catch-up fires, next fire at next natural occurrence. |
| [quartznet#761](https://github.com/quartznet/quartznet/issues/761), [#641](https://github.com/quartznet/quartznet/issues/641), [#433](https://github.com/quartznet/quartznet/issues/433), [#594](https://github.com/quartznet/quartznet/issues/594) | Group-level pause state is sticky, survives member deletion, swallows later triggers. **Lesson:** Bifrost has no groups — keep pause state strictly on the job record; if groups ever arrive, no implicit persistent group state. |
| [quartznet#852](https://github.com/quartznet/quartznet/issues/852)/[#2202](https://github.com/quartznet/quartznet/issues/2202) | Jobs stuck in internal transient states forever under load. **Test:** state-machine invariant — every transient state has a guaranteed timed exit path. |
| [Hangfire#225](https://github.com/HangfireIO/Hangfire/issues/225) | 46-comment, years-open request for first-class pause. Confirms pause/resume as a real differentiator; preserve definition + declare missed-occurrence behavior (done — see plan decision). |

### 6.7 Registration races / dynamic mutation

| Source | Lesson → test case |
|---|---|
| [NCronJob#100](https://github.com/NCronJob-Dev/NCronJob/issues/100) | Runtime-added jobs appeared in registry but never ran (loop not woken). Validates the DR-7 command-channel wake design. **Test:** add at runtime due <1 interval → fires; remove mid-wait → doesn't fire. |
| [NCronJob#253](https://github.com/NCronJob-Dev/NCronJob/issues/253) | Duplicate detection keyed on type, not (type, name) → crash at execution. **Test:** N registrations of one job type under distinct names all run independently. |
| [quartznet#2629](https://github.com/quartznet/quartznet/issues/2629) | Job/trigger registration order nondeterministic via options pattern → intermittent startup failure. **Test:** fluent registrations validate as a unit regardless of order. |
| [quartznet#2180](https://github.com/quartznet/quartznet/issues/2180) | Dynamically-added past-due trigger fired immediately; user expected skip. **Test/API:** registering with a past `At(...)` follows an explicit, visible policy (reject or skip — never silent fire-now). |
| [Hangfire#1637](https://github.com/HangfireIO/Hangfire/issues/1637) | `AddOrUpdate` triggered an immediate run. **Test:** update with unchanged/future schedule never causes immediate execution. |
| [quartznet#3028](https://github.com/quartznet/quartznet/issues/3028)/[#781](https://github.com/quartznet/quartznet/issues/781)/[#800](https://github.com/quartznet/quartznet/issues/800) | Scheduler silently stops firing under thread-pool starvation; 40-comment production-hang thread. **Test:** liveness — under induced starvation, jobs fire OR health check reports Unhealthy. Validates DR-8's tick-staleness health check; "stops silently" must be unrepresentable. |

### 6.8 Ergonomics lessons (API designs that invited user error)

- **Fire-now triggered by a write is the #1 surprise generator** (quartznet#1545/#636/#2180, Hangfire#1637): any immediate execution caused by register/reschedule/resume must be loud — logged, opt-in, or a required policy parameter.
- **Invisible global thresholds** (Quartz's 60s misfire threshold) create behavioral cliffs; Bifrost's threshold-free per-job policies avoid this — keep it that way.
- **Cron dialect surprises** ([coravel#250](https://github.com/jamesmh/coravel/issues/250) — even the maintainer misread `* */1 * * *`; [Hangfire#899](https://github.com/HangfireIO/Hangfire/issues/899) — dashboard showed a different next-execution than what ran): validate at registration (already DR-2), **surface the computed next N occurrences** at registration/inspection (add to `IBifrostScheduleInspector`), and use one cron engine for both display and firing.
- **Unsupported feature combinations must fail at configuration time**, not NRE at runtime ([coravel#91](https://github.com/jamesmh/coravel/issues/91)).
- **Silent exception swallowing** ([NCronJob#23](https://github.com/NCronJob-Dev/NCronJob/issues/23)): default must be log-and-surface with a global hook (Bifrost: `JobFireFailedEvent` — already designed; ensure default logging too).
- **Registration APIs that look composable but aren't should throw** ([NCronJob#138](https://github.com/NCronJob-Dev/NCronJob/issues/138)): define double-`AddScheduler` behavior explicitly.
- **Overlap-guard lease durations must be configurable and documented** ([coravel#187](https://github.com/jamesmh/coravel/issues/187)/[#351](https://github.com/jamesmh/coravel/issues/351) — hidden 24h timeout failed both directions).
- **Host suspension is invisible to in-process schedulers** ([coravel#211](https://github.com/jamesmh/coravel/issues/211)/[#388](https://github.com/jamesmh/coravel/issues/388) — IIS idle-timeout "skipped weekends"): docs must call out IIS/App Service idle behavior; the tick-staleness health check is the mitigation.

---

## 7. Recommendations

Keyed R1–R6 to the design-review findings that motivated this spike; R7–R10 are new from research.

| # | Finding | Recommendation | Severity |
|---|---|---|---|
| **R1** | `Cadence.After` uses `DateTimeOffset.UtcNow` (design line ~251), violating DR-7's TimeProvider law | Resolve `After` at **registration time** in the registry (which owns `TimeProvider`); `OneShotCadence` stores only the resolved absolute time. Add a banned-API analyzer/arch-test for `DateTime*.Now/UtcNow` in production code — NCronJob#169 shows partial TimeProvider adoption is a recurring real-world defect class. | Must-fix in design |
| **R2** | Zero-alloc target (DR-11) contradicts the tick-loop pseudo-code (per-cycle CTS/Task allocations) | Research reframes: **no incumbent competes on allocations** — perf is not the differentiating axis; the harness, missed-fire, and AOT are. Keep the O(log n) heap and the correctness-under-load test (Hangfire#751: 1000+ simultaneous due jobs, none lost). Scope "0 B/fire" to steady-state dispatch handoff only, or demote to aspirational benchmark target rather than acceptance criterion. Add early-wake clamping (NCronJob#327: `Task.Delay` wakes early on Windows → duplicate fires) — never compute next fire from a pre-schedule timestamp. | Revise DR-11 |
| **R3** | Delivery semantics unstated | Adopt **at-least-once per occurrence** as a named requirement with the industry-standard blunt phrasing (Hangfire/Sidekiq/EventBridge precedents in §2). Document the duplicate window (crash between dispatch and checkpoint). Name `(JobName, ScheduledFireTime)` as the idempotency key in `JobFireContext` docs; FireTime = scheduled occurrence time, stable across re-fires. | Add to design (new DR or DR-10 amendment) |
| **R4** | `ILeadershipLease` has no fencing token; `bool IsHeld` is the Kleppmann local-knowledge anti-pattern | **Defer `IExclusiveScheduleStore` + `ILeadershipLease` from the v1 public surface.** Document the v1 posture: single-process scheduling, multi-process = duplicate fires + startup warning (already DR-6). When the Marten adapter forces the contract: epoch/fencing token, `LastRenewedAt`/`ExpiresAt` not bare `IsHeld`, store-side conditional checkpoint writes, abstract class over interface (IDistributedCache→HybridCache precedent). | Must-fix in design (removes API surface) |
| **R5** | "Timer-wheel" misnomer | Rename to "min-heap tick engine" / "priority-queue scheduler" throughout. Note for posterity: real timer wheels (Varghese & Lauck) are O(1) and matter at ~100K+ timers; the heap is correct at target scale. | Editorial |
| **R6** | "Durable" naming collision | Drop "durable" from feature positioning (§5): collides with the durable-execution category and Microsoft's "Durable Task Scheduler" product; v1 is in-memory-only and literally not durable. Position as "Bifrost Scheduling — pluggable schedule persistence with at-least-once recovery." Package names (`Bifrost.Scheduling*`) are already fine. | Editorial, but user-facing |
| **R7** | Competitive analysis was Wolverine-only; landscape moved | Add an honest positioning section to the design (and eventually README): the four real differentiators (orchestrator dispatch, interval+jitter cadences, per-job missed-fire policies, deterministic harness, AOT end-to-end) and **when NOT to use** (not on Bifrost orchestrators → NCronJob or TickerQ). Update the issue-#16 gap framing. | Add to design |
| **R8** | AOT absent from design despite being a top-tier differentiator | Promote to a named DR: every shipped package declares `IsAotCompatible=true`; CI validates with `PublishAot` smoke + trim-warnings-as-errors. Use only the defensible claims from §3 (never "first/only"). Constrain the future Marten adapter: no reflection-based serialization of `JobRecord`/`Cadence` — source-generated contracts (this is the claim TickerQ cannot make: its stores are EF Core/Redis). Cronos verified AOT-safe by construction (full source audit). | Add to design (new DR) |
| **R9** | DR-10's edge-case list was written from first principles only | Fold §6 into the DR-10 acceptance criteria / plan test matrix. Highest-leverage additions beyond current DR-10: DST fall-back fire-count + spring-forward monotonic-progress property tests (quartznet#2475/#2497 — the former still unfixed in Quartz); early-wake clamp; one-job-failure-never-poisons-the-loop (Hangfire#530 crash-looped ALL jobs); shutdown injection at every await point; idempotent StopAsync under start/stop hammering; 1000-simultaneous-due-jobs lost-fire test; resume-fires-nothing pin test. | Amend DR-10 + plan |
| **R10** | Ergonomics traps recur across all five incumbents | Adopt §6.8 rules: no silent fire-now on any write op (explicit policy for past-due registration); next-N-occurrences preview on `IBifrostScheduleInspector`; config-time failure for unsupported combinations; explicit double-`AddScheduler` behavior; document IIS/App Service idle-timeout behavior alongside the health check. | Add to design + plan tasks |

### Suggested next step

Run a design-revision pass on `docs/designs/2026-04-10-durable-scheduling-api.md` applying R1–R10 (R4 removes ~30 lines of API surface; R3/R8 add two requirements; R9 expands the test matrix), then regenerate the affected plan tasks before initializing the implementation workflow.

---

## Appendix: full source list

### Feature matrix
NCronJob: repo, docs.ncronjob.dev (dynamic-job-control, define-and-schedule-jobs), NuGet, Giesel blog (June edition, Big Updates). TickerQ: repo + README + releases, tickerq.net (dashboard), issues #83/#317, antondevtips comparison, ABP integration docs. Coravel: repo, docs.coravel.net/Scheduler, NuGet, issue #143. Quartz.NET: repo + releases, more-about-triggers / simpletriggers / crontriggers / 4.x migration guide / FAQ, NuGet, discussion #2198 (trimming), OTel contrib package. Hangfire: docs (background-methods), NuGet, licenses, release blog, forum (MisfireHandlingMode), phillduffy.com/hangfire-missed-jobs, OTel contrib. FluentScheduler repo/NuGet; Sundial (Gitee); daily-devops.net 7-part 2026 comparison series.

### Issue mining
quartznet: #1545 #636 #3096 #1109 #1758 #128 #2475 #2497 #332 #2349 #539 #2156 #1508 #2034 #703 #435 #2804 #1781 #1740 #623 #499 #2697 #761 #641 #433 #594 #852 #2202 #2629 #2180 #3028 #781 #800. Hangfire: #546 #751 #567 #530 #529 #537 #2330 #2452 #2378 #1298 #1960 #90 #936 #1852 #225 #1637 #899. TickerQ: #83 #33 #458 #187 #274 #286 #234 #317. NCronJob: #327 #169 #100 #253 #172 #23 #98 #138 #221. Coravel: #391 #221 #219 #77 #250 #91 #187 #351 #211 #388. Repro repos noted: kkloet1/QuartzNetDST, ewortzman/quartz-blob-bug, mbcrawfo/tickerq-duplication, IvaTutis/NChronJobBugExample, matelq/TickerQ.Jobs.

### AOT audit
quartznet/quartznet src/Quartz/Quartz.csproj + Util/ObjectUtils.cs + SimpleTypeLoadHelper; HangfireIO/Hangfire #2478 + src/Hangfire.Core (Job.cs, TypeHelper.cs, InvocationData.cs, ExpressionUtil/); jamesmh/coravel #453 + ScheduledEvent.cs + Events/Dispatcher.cs; NCronJob-Dev/NCronJob (DynamicJobFactory.cs, ConditionInvokerBuilder.cs); Arcenox-co/TickerQ (csproj files, samples/TickerQ.Sample.Dashboard.ReflectionFree, issues #830 #863 #837 #793 #764 #606); fluentscheduler/FluentScheduler version-6 branch; HangfireIO/Cronos full source audit; MS Learn: EF Core NativeAOT (experimental status), creating-aot-compatible-libraries, native-aot deployment.

### Delivery semantics / fencing / naming
Kleppmann "How to do distributed locking"; quartz-scheduler.org ConfigJDBCJobStoreClustering; quartz-scheduler.net configuration reference + best-practices; hangfire.io "Are your methods ready to run in background?" + throttling docs + issues #2107 #1799 #998 + forum (long-running jobs lock timeout); sidekiq/sidekiq wiki Best-Practices; docs.temporal.io (activities, activity-definition) + temporal.io/blog/what-is-durable-execution; AWS EventBridge Scheduler UserGuide + event-delivery-level; GCP Cloud Scheduler overview; k8s.io/client-go/tools/leaderelection; PostgreSQL explicit-locking docs; wolverinefx.net (leader-election tutorial, durability/leadership-and-troubleshooting); JasperFx/marten PR #1780; MS Learn durable-task (what-is, scheduler) + techcommunity DTS GA announcement; Dapr workflow overview; kai-waehner.de durable-execution-engine post; golem.cloud emerging-landscape post; InfoQ AWS Lambda Durable Functions (Dec 2025); dotnet/aspnetcore #53255 (HybridCache epic); MS Learn hybrid cache.

### Known data-quality caveats
- Quartz.NET release dates rendered inconsistently across fetches (relative dates); 3.18.1 = 2026-04-25 per NuGet, treated as authoritative.
- TickerQ cron-timezone support and health-check support: not found in docs — recorded as "not documented," not confirmed absent.
- Quartz "resume fires immediately" cites documented misfire behavior + adjacent threshold issue #1109; no single dedicated GitHub issue exists (complaints live mostly on StackOverflow).
