# Implementation Plan: Bifrost Scheduling API

**Design:** `docs/designs/2026-04-10-durable-scheduling-api.md`
**Issue:** [#16](https://github.com/lvlup-sw/bifrost/issues/16)
**Revised:** 2026-06-12 to match design revision R1–R10; see `docs/research/2026-06-12-durable-scheduling-precedents.md`. (The file name keeps the historical `durable-scheduling` slug — see the design's naming note, R6.)
**Iron Law:** No production code without a failing test first.

---

## Source Design

See `docs/designs/2026-04-10-durable-scheduling-api.md`.

## Scope

**Target:** MVP slice of the full design (per brainstorming consensus: "design full, ship MVP").

**Included (this plan):**
- New packages: `Bifrost.Scheduling.Core`, `Bifrost.Scheduling`, `Bifrost.Scheduling.Testing`
- New test project: `Bifrost.Tests.Scheduling`
- Full `IScheduleRegistry` (fluent DI + runtime API) — DR-1
- All cadence primitives (interval, cron, one-shot, jittered) — DR-2
- All three missed-fire policies — DR-3
- All three dispatch modes (orchestrator, inline, custom) — DR-4
- `IScheduleStore` contract + `InMemoryScheduleStore` — DR-5
- Single-process always-leader behavior + multi-instance startup warning — DR-6 (the coordination contract does **not** ship in v1 at all; deferred per R4)
- `ScheduleTickLoop` with priority-queue (min-heap) engine and wake-channel for mutations — DR-7
- Observability: metrics, events, health check, inspector API (incl. `GetNextOccurrences`) — DR-8
- `ISchedulerTestHarness` with `FakeTimeProvider` support — DR-9
- Full error-handling surface, expanded per the mined edge-case inventory (research §6 / R9) — DR-10
- Benchmark suite in `Bifrost.Benchmarks/Scheduling/` — DR-11 (revised per R2)
- At-least-once delivery semantics with stable `(JobName, FireTime)` idempotency key — DR-12 (new, R3)
- End-to-end AOT validation: `PublishAot` smoke + trim-warnings-as-errors — DR-13 (new, R8)
- Design-revision additions (2026-06-12, Tasks 45–51): registration-time `Cadence.After` resolution (R1), banned-API architecture test (R1), early-wake clamp (R2), DR-10 hardening (R9), R10 ergonomics

**Excluded (follow-up releases, explicitly deferred):**
- `Bifrost.Scheduling.Marten` adapter package — deferred to follow-up release.
- `Bifrost.Scheduling.Wolverine` interop adapter — deferred per issue #16 "Wolverine interop surface" section.
- Any persistent storage implementation beyond in-memory — deferred; the `IScheduleStore` contract is the forward-compatibility boundary.
- Multi-instance coordination — deferred **entirely, including the contract** (DR-6/R4). No coordination interface, lease type, or leader-election API appears in the v1 public surface; the contract is designed together with the Marten adapter, not before it.

## Summary

- **Total tasks:** 50 active (51 numbered — Task 8 removed per design R4, tombstone retained to keep task numbering and dependency references stable)
- **Parallel groups:** 13 (A through M)
- **Estimated test count:** ~160
- **Design coverage:** 13 of 13 DR-N requirements covered

---

## Spec Traceability

### Design Requirement → Task Mapping

| DR | Requirement | Tasks |
|----|-------------|-------|
| DR-1 | Job registry with fluent and runtime APIs | 5, 6, 15, 19, 20, 21, 38, 39, 51 |
| DR-2 | Cadence primitives | 9, 10, 11, 12, 13, 38, 43, 45, 48 |
| DR-3 | Missed-fire policies | 5, 14, 26, 38 |
| DR-4 | Pluggable dispatch — orchestrator, inline, and custom | 16, 22, 23, 24, 30, 38, 40, 44 |
| DR-5 | Pluggable storage via IScheduleStore | 6, 7, 18, 39 |
| DR-6 | Coordination — single-process v1; coordination contract deferred (R4) | 5, 27, 39 |
| DR-7 | Min-heap tick engine | 25, 26, 42, 46, 47 |
| DR-8 | Observability — metrics, events, and health checks | 17, 31, 32, 33, 34, 51 |
| DR-9 | Testing primitives — FakeTimeProvider + deterministic fire control | 3, 35, 36, 37 |
| DR-10 | Error handling and edge cases | 13, 26, 28, 29, 30, 48 |
| DR-11 | Performance targets (revised per R2) | 41, 42, 43, 44, 48 |
| DR-12 | Delivery semantics — at-least-once per occurrence (new, R3) | 49 |
| DR-13 | AOT compatibility — end to end (new, R8) | 50 |

### Design Section → Task Mapping

This table maps every Technical Design subsection of `docs/designs/2026-04-10-durable-scheduling-api.md` to implementing tasks.

| Design Section | Tasks |
|----------------|-------|
| Package layout | 1, 2, 3, 4 (scaffolds all three packages + test project per the Package layout diagram) |
| Type model | 5, 6, 7, 9, 10, 11, 12, 15, 16, 17, 45 (all types in the Type model code block; the coordination contract is intentionally absent — DR-6/R4) |
| Tick loop (pseudo-code) | 25, 26, 27, 28, 29, 30, 47, 48, 49 (implements the `ScheduleTickLoop` pseudo-code loop) |
| Fluent builder integration | 38, 39 (`ISchedulerBuilder`, `AddJob<TWork>`, `AddInlineJob`, `UseStore<T>`) |
| DR-1: Job registry with fluent and runtime APIs | 5, 6, 15, 19, 20, 21, 38, 39, 51 |
| DR-2: Cadence primitives | 9, 10, 11, 12, 13, 38, 43, 45, 48 |
| DR-3: Missed-fire policies | 5, 14, 26, 38 |
| DR-4: Pluggable dispatch — orchestrator, inline, and custom | 16, 22, 23, 24, 30, 38, 40, 44 |
| DR-5: Pluggable storage via IScheduleStore | 6, 7, 18, 39 |
| DR-6: Coordination — single-process v1; coordination contract deferred (R4) | 5, 27, 39 |
| DR-7: Min-heap tick engine | 25, 26, 42, 46, 47 |
| DR-8: Observability — metrics, events, and health checks | 17, 31, 32, 33, 34, 51 |
| DR-9: Testing primitives — FakeTimeProvider + deterministic fire control | 3, 35, 36, 37 |
| DR-10: Error handling and edge cases | 13, 26, 28, 29, 30, 48 |
| DR-11: Performance targets (revised per R2) | 41, 42, 43, 44, 48 |
| DR-12: Delivery semantics — at-least-once per occurrence (new, R3) | 49 |
| DR-13: AOT compatibility — end to end (new, R8) | 50 |

---

## Group A: Package Scaffolding (Package layout)

Foundation tasks — must run sequentially. No production code yet. Implements the **Package layout** section of the design document.

### Task 1: Create Bifrost.Scheduling.Core project (Package layout)
**Phase:** Scaffolding (no tests — project shell only)
**Test Layer:** n/a
**Implements:** All DRs (infrastructure)

**Steps:**
1. Create `src/Bifrost.Scheduling.Core/Bifrost.Scheduling.Core.csproj`
   - `<TargetFramework>net10.0</TargetFramework>`
   - `<IsPackable>true</IsPackable>`
   - `<Nullable>enable</Nullable>`
   - `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`
   - Package metadata consistent with `Bifrost.Core.csproj`
2. Add project to `src/Bifrost.sln`
3. Add `Microsoft.Extensions.DependencyInjection.Abstractions` package reference (for `IServiceProvider` in `JobFireContext`)
4. Verify build: `dotnet build src/Bifrost.Scheduling.Core/`

**Verification:**
- [ ] Project file exists and builds clean
- [ ] Added to solution

**Dependencies:** None
**Parallelizable:** No (sequential within Group A)

---

### Task 2: Create Bifrost.Scheduling project
**Phase:** Scaffolding
**Test Layer:** n/a
**Implements:** All DRs (infrastructure)

**Steps:**
1. Create `src/Bifrost.Scheduling/Bifrost.Scheduling.csproj`
   - Same conventions as Task 1
2. Add ProjectReferences:
   - `../Bifrost.Scheduling.Core/Bifrost.Scheduling.Core.csproj`
   - `../Bifrost.Core/Bifrost.Core.csproj` (for `IWorkOrchestrator<TWork>`)
   - `../Bifrost/Bifrost.csproj` (for event stream infrastructure)
3. Add package references:
   - `Cronos` (cron expression parsing — see DR-2 open question, Cronos is the chosen default)
   - `Microsoft.Extensions.Hosting.Abstractions` (for `BackgroundService` in tick loop)
   - `Microsoft.Extensions.Logging.Abstractions`
   - `Microsoft.Extensions.Options`
4. Add to solution
5. Verify build

**Verification:**
- [ ] Project builds
- [ ] Cronos resolved correctly

**Dependencies:** Task 1
**Parallelizable:** No

---

### Task 3: Create Bifrost.Scheduling.Testing project
**Phase:** Scaffolding
**Test Layer:** n/a
**Implements:** DR-9

**Steps:**
1. Create `src/Bifrost.Scheduling.Testing/Bifrost.Scheduling.Testing.csproj`
2. Add ProjectReferences:
   - `../Bifrost.Scheduling/Bifrost.Scheduling.csproj`
3. Add package references:
   - `Microsoft.Extensions.TimeProvider.Testing` (for `FakeTimeProvider`)
4. Add to solution
5. Verify build

**Dependencies:** Task 2
**Parallelizable:** No

---

### Task 4: Create Bifrost.Tests.Scheduling project
**Phase:** Scaffolding
**Test Layer:** n/a
**Implements:** All DRs (test infrastructure)

**Steps:**
1. Create `src/Bifrost.Tests.Scheduling/Bifrost.Tests.Scheduling.csproj`
   - `<IsPackable>false</IsPackable>`
2. Copy test project conventions from `src/Bifrost.Tests/Bifrost.Tests.csproj`:
   - TUnit package references
   - Coverage configuration
3. Add ProjectReferences:
   - `../Bifrost.Scheduling/Bifrost.Scheduling.csproj`
   - `../Bifrost.Scheduling.Core/Bifrost.Scheduling.Core.csproj`
   - `../Bifrost.Scheduling.Testing/Bifrost.Scheduling.Testing.csproj`
   - `../Bifrost/Bifrost.csproj` (for orchestrator integration tests)
4. Add `Microsoft.Extensions.TimeProvider.Testing`
5. Add to solution
6. Verify `dotnet test src/Bifrost.Tests.Scheduling/` runs (empty suite passes)

**Dependencies:** Task 3
**Parallelizable:** No

---

## Group B: Core Contracts (Bifrost.Scheduling.Core) — Type model

Implements the **Type model** section of the design document. All public types for the scheduler contracts: enums, records, interfaces, exceptions, and event structs. Each contract is a small TDD task: write a test asserting the type exists and has the expected shape, then create the type.

### Task 5: Enums — JobState, MissedFirePolicy (Type model)
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** unit
**Implements:** DR-1, DR-3, DR-6

1. **[RED]** `src/Bifrost.Tests.Scheduling/Core/EnumsTests.cs`
   - `JobState_HasExpectedMembers` — verify `Running`, `Paused`, `Faulted`, `Completed` values
   - `MissedFirePolicy_HasExpectedMembers` — verify `Coalesce`, `FireAllMissed`, `SkipMissed` values
   - `MissedFirePolicy_CoalesceIsDefault` — `default(MissedFirePolicy) == MissedFirePolicy.Coalesce`
   - Expected failure: types don't exist

2. **[GREEN]**
   - `src/Bifrost.Scheduling.Core/JobState.cs` — public enum with 4 members
   - `src/Bifrost.Scheduling.Core/MissedFirePolicy.cs` — public enum with 3 members, `Coalesce = 0`

3. **[REFACTOR]** XML docs

**Dependencies:** Task 1
**Parallelizable:** Yes

---

### Task 6: JobRecord type
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** unit
**Implements:** DR-1, DR-5

1. **[RED]** `src/Bifrost.Tests.Scheduling/Core/JobRecordTests.cs`
   - `JobRecord_Construction_SetsAllProperties` — construct with all fields, verify accessible
   - `JobRecord_WithState_ReturnsNewRecordWithUpdatedState` — non-destructive mutation via `with`
   - `JobRecord_EqualityByValue` — two records with same fields are equal
   - `JobRecord_ImplementsIEquatable` — standard record semantics
   - Expected failure: `JobRecord` type does not exist

2. **[GREEN]** `src/Bifrost.Scheduling.Core/JobRecord.cs`
   ```csharp
   public sealed record JobRecord(
       string Name,
       Cadence Cadence,
       MissedFirePolicy MissedFirePolicy,
       JobState State,
       DateTimeOffset? LastFiredAt,
       DateTimeOffset? NextFireAt,
       string DispatchKind,
       string? DispatcherTypeName,
       IReadOnlyDictionary<string, string> Metadata);
   ```
   Note: `Cadence` type created in Task 9 — for this task, create a minimal `Cadence` stub (abstract class with no members) so `JobRecord` compiles. Task 9 will expand it.

3. **[REFACTOR]** XML docs, parameter null annotations

**Dependencies:** Task 1, Task 5
**Parallelizable:** Yes (with other Group B tasks after stub created)

---

### Task 7: IScheduleStore interface
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** unit
**Implements:** DR-5

1. **[RED]** `src/Bifrost.Tests.Scheduling/Core/IScheduleStoreTests.cs`
   - `IScheduleStore_IsInterface`
   - `IScheduleStore_HasLoadAllAsyncMethod` — signature `ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken)`
   - `IScheduleStore_HasSaveAsyncMethod` — signature `ValueTask SaveAsync(JobRecord, CancellationToken)`
   - `IScheduleStore_HasRecordFiredAsyncMethod` — signature `ValueTask RecordFiredAsync(string, DateTimeOffset, DateTimeOffset?, CancellationToken)`
   - `IScheduleStore_HasDeleteAsyncMethod` — signature `ValueTask DeleteAsync(string, CancellationToken)`
   - Expected failure: interface does not exist

2. **[GREEN]** `src/Bifrost.Scheduling.Core/IScheduleStore.cs`

3. **[REFACTOR]** XML docs on every member with contract guarantees documented

**Dependencies:** Task 6
**Parallelizable:** Yes

---

### Task 8: REMOVED per design R4 (2026-06-12) — coordination contract deferred to the Marten adapter
**Phase:** n/a (tombstone)
**Test Layer:** n/a
**Implements:** n/a

This task previously defined the `IExclusiveScheduleStore` + leadership-lease contracts. Per the
revised design (DR-6 / R4), the coordination contract does **not** ship in v1 at all — not even as
an unimplemented interface. It is designed together with the first durable adapter
(`Bifrost.Scheduling.Marten`), where the real store informs the shape (epoch/fencing token,
`LastRenewedAt`/`ExpiresAt` instead of a bare held-flag, conditional checkpoint writes, abstract
base class over interface). The tombstone is retained so task numbering and dependency references
across this plan remain stable.

**Dependencies:** None (tombstone)
**Parallelizable:** n/a

---

## Group C: Cadence primitives — implementations

Implements **DR-2: Cadence primitives**. All cadence variants — interval, cron, one-shot, jittered — with `ComputeNextFire` semantics.

### Task 9: Cadence primitives — abstract base + OneShotCadence
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** unit
**Implements:** DR-2

1. **[RED]** `src/Bifrost.Tests.Scheduling/Cadences/OneShotCadenceTests.cs`
   - `OneShotCadence_ComputeNextFire_WhenLastFiredNull_ReturnsFireAt`
   - `OneShotCadence_ComputeNextFire_WhenLastFiredNotNull_ReturnsNull` — one-shot doesn't re-fire
   - `OneShotCadence_ComputeNextFire_WhenFireAtInPast_StillReturnsFireAt` — tick loop decides what to do with past fires
   - `Cadence_At_ReturnsOneShotCadence`
   - `Cadence_After_ReturnsRelativeOneShotCadence` — the static factory performs **no clock access** (R1/DR-2); registration-time resolution via `TimeProvider` is tested in Task 45
   - Expected failure: types don't exist / are stubs from Task 6

2. **[GREEN]**
   - Replace `Cadence` stub with `public abstract record Cadence` exposing abstract `ComputeNextFire(DateTimeOffset? lastFiredAt, DateTimeOffset now)`
   - Add static factory methods `Interval`, `Cron`, `At`, `After` (stubs for non-OneShot types; filled in later tasks)
   - `src/Bifrost.Scheduling.Core/OneShotCadence.cs` — `public sealed record OneShotCadence(DateTimeOffset FireAt) : Cadence`
   - `src/Bifrost.Scheduling.Core/RelativeOneShotCadence.cs` — `public sealed record RelativeOneShotCadence(TimeSpan Delay) : Cadence`; its `ComputeNextFire` throws `InvalidOperationException` (an unresolved relative cadence must never reach the tick loop — the registry resolves it to an absolute `OneShotCadence` at registration, Task 45)

3. **[REFACTOR]** XML docs on Cadence contract

**Dependencies:** Task 6
**Parallelizable:** No (other cadence tasks depend on this)

---

### Task 10: Cadence primitives — IntervalCadence (no jitter)
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** unit
**Implements:** DR-2

1. **[RED]** `src/Bifrost.Tests.Scheduling/Cadences/IntervalCadenceTests.cs`
   - `IntervalCadence_ComputeNextFire_FirstRun_ReturnsNowPlusInterval` — when `lastFiredAt` is null
   - `IntervalCadence_ComputeNextFire_SubsequentRun_ReturnsLastFiredPlusInterval`
   - `IntervalCadence_ComputeNextFire_ResultIsStrictlyGreaterThanNow` — when last+interval < now, returns next aligned fire
   - `IntervalCadence_Constructor_ZeroInterval_Throws`
   - `IntervalCadence_Constructor_NegativeInterval_Throws`
   - `Cadence_Interval_ReturnsIntervalCadence`

2. **[GREEN]** `src/Bifrost.Scheduling.Core/IntervalCadence.cs`
   - `public sealed record IntervalCadence(TimeSpan Interval, double Jitter = 0) : Cadence`
   - `ComputeNextFire` logic (jitter handled in Task 11)

3. **[REFACTOR]** XML docs

**Dependencies:** Task 9
**Parallelizable:** No

---

### Task 11: Cadence primitives — IntervalCadence jitter
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** unit + property
**Implements:** DR-2

1. **[RED]** `src/Bifrost.Tests.Scheduling/Cadences/IntervalCadenceJitterTests.cs`
   - `IntervalCadence_WithJitter_ValidFraction_ReturnsCadenceWithJitter` — `.WithJitter(0.2)`
   - `IntervalCadence_WithJitter_NegativeFraction_Throws`
   - `IntervalCadence_WithJitter_GreaterThanOne_Throws`
   - `IntervalCadence_ComputeNextFire_WithJitter_StaysWithinBounds` — over 1000 samples with jitter=0.2, all fires are in `[interval*0.8, interval*1.2]`
   - **Property test:** for any `interval > 0` and `jitter in [0, 1]`, `ComputeNextFire` result deviates from expected by at most `interval * jitter`

2. **[GREEN]**
   - Update `IntervalCadence.ComputeNextFire` to apply jitter using injected randomness (pass `Random` or use `Random.Shared`)
   - Add `WithJitter(double fraction)` factory method on `IntervalCadence`

3. **[REFACTOR]** Extract jitter-computation helper

**testingStrategy:** `propertyTests: true`

**Dependencies:** Task 10
**Parallelizable:** No

---

### Task 12: Cadence primitives — CronCadence (Cronos-backed)
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** unit
**Implements:** DR-2

1. **[RED]** `src/Bifrost.Tests.Scheduling/Cadences/CronCadenceTests.cs`
   - `CronCadence_ComputeNextFire_EveryMinute_ReturnsNextMinuteBoundary`
   - `CronCadence_ComputeNextFire_Daily9am_ReturnsNext9am`
   - `CronCadence_ComputeNextFire_RespectsTimeZone` — pass an EST TZ, verify fire time translated
   - `CronCadence_ComputeNextFire_DefaultUtc` — no TZ argument means UTC
   - `CronCadence_Constructor_InvalidExpression_Throws` — fail fast
   - `CronCadence_Constructor_NullExpression_Throws`
   - `Cadence_Cron_ReturnsCronCadence`

2. **[GREEN]** `src/Bifrost.Scheduling.Core/CronCadence.cs`
   - Uses `Cronos.CronExpression` for parsing
   - Stores parsed expression, not raw string
   - Throws on parse failure at construction time

3. **[REFACTOR]** XML docs; note that cron expressions use Cronos syntax (5 or 6 fields)

**Dependencies:** Task 9 (and Cronos package ref from Task 2)
**Parallelizable:** Yes (with Task 10 once Task 9 done)

---

### Task 13: Cadence primitives — DST transition correctness for CronCadence
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** unit
**Implements:** DR-2, DR-10

1. **[RED]** `src/Bifrost.Tests.Scheduling/Cadences/CronCadenceDstTests.cs`
   - `CronCadence_DstSpringForward_SkippedHour_DoesNotDuplicate` — 2am UTC→EST jump, job set for 2:30am
   - `CronCadence_DstFallBack_RepeatedHour_DoesNotDouble` — 1:30am rollback, verify no duplicate fire
   - Relies on Cronos's existing DST handling — these tests codify contract

2. **[GREEN]** No implementation changes expected; this validates Cronos behavior

3. **[REFACTOR]** N/A

**Dependencies:** Task 12
**Parallelizable:** Yes (after Task 12)

---

### Task 14: Missed-fire policy computation helper
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** unit
**Implements:** DR-3

1. **[RED]** `src/Bifrost.Tests.Scheduling/MissedFirePolicyApplierTests.cs`
   - `Applier_Coalesce_MultipleMissedFires_ReturnsOneCatchUp`
   - `Applier_FireAllMissed_MultipleMissedFires_ReturnsAllMissed`
   - `Applier_SkipMissed_MultipleMissedFires_ReturnsNextFutureFire`
   - `Applier_NoMissedFires_ReturnsEmpty` — `lastFiredAt + interval > now`
   - `Applier_OneShotCadence_NoMissedFireConcept_ReturnsEmpty`
   - `Applier_FireAllMissed_CapsAtMaxCount` — safety cap (default 100) to prevent pathological catch-up storms
   - Expected failure: `MissedFirePolicyApplier` does not exist

2. **[GREEN]** `src/Bifrost.Scheduling/Internal/MissedFirePolicyApplier.cs`
   - Internal static class
   - `ComputeMissedFires(Cadence cadence, DateTimeOffset? lastFiredAt, DateTimeOffset now, MissedFirePolicy policy, int maxCatchUpCount = 100) → IReadOnlyList<DateTimeOffset>`

3. **[REFACTOR]** Extract `Cadence.EnumerateFiresBetween` helper if reused

**Dependencies:** Tasks 9–12
**Parallelizable:** No

---

## Group D: IScheduleRegistry Contract + Dispatch Contracts

### Task 15: IScheduleRegistry interface + DuplicateJobNameException
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** unit
**Implements:** DR-1

1. **[RED]** `src/Bifrost.Tests.Scheduling/Core/IScheduleRegistryTests.cs`
   - `IScheduleRegistry_IsInterface`
   - `IScheduleRegistry_HasRegisterAsyncMethod`
   - `IScheduleRegistry_HasUnregisterAsyncMethod`
   - `IScheduleRegistry_HasPauseAsyncMethod`
   - `IScheduleRegistry_HasResumeAsyncMethod`
   - `IScheduleRegistry_HasTriggerAsyncMethod`
   - `IScheduleRegistry_HasGetJobsMethod` — returns `IReadOnlyList<JobDescriptor>`
   - `IScheduleRegistry_HasGetJobMethod` — returns `JobDescriptor?` for missing
   - `DuplicateJobNameException_IsException`
   - `DuplicateJobNameException_HasJobNameProperty`

2. **[GREEN]**
   - `src/Bifrost.Scheduling.Core/IScheduleRegistry.cs`
   - `src/Bifrost.Scheduling.Core/JobDescriptor.cs` — readonly record struct with name, state, cadence, lastFiredAt, nextFireAt, isRunning
   - `src/Bifrost.Scheduling.Core/DuplicateJobNameException.cs`

3. **[REFACTOR]** XML docs

**Dependencies:** Task 6
**Parallelizable:** Yes

---

### Task 16: IJobDispatcher + JobFireContext
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** unit
**Implements:** DR-4

1. **[RED]** `src/Bifrost.Tests.Scheduling/Core/IJobDispatcherTests.cs`
   - `IJobDispatcher_IsInterface`
   - `IJobDispatcher_HasDispatchAsyncMethod` — signature `ValueTask DispatchAsync(JobFireContext, CancellationToken)`
   - `JobFireContext_IsReadonlyRecordStruct`
   - `JobFireContext_HasJobNameProperty`
   - `JobFireContext_HasFireTimeProperty`
   - `JobFireContext_HasNextFireAtProperty`
   - `JobFireContext_HasServicesProperty`
   - `JobFireContext_Construction_SetsAllFields`

2. **[GREEN]**
   - `src/Bifrost.Scheduling.Core/IJobDispatcher.cs`
   - `src/Bifrost.Scheduling.Core/JobFireContext.cs`

3. **[REFACTOR]** XML docs

**Dependencies:** Task 1
**Parallelizable:** Yes

---

### Task 17: Scheduler event types
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** unit
**Implements:** DR-8

1. **[RED]** `src/Bifrost.Tests.Scheduling/Events/SchedulerEventsTests.cs`
   - `JobFiredEvent_RecordStruct_HasFields`
   - `JobFireFailedEvent_RecordStruct_HasFields`
   - `JobMissedFireEvent_RecordStruct_HasFields`
   - `JobRegisteredEvent_RecordStruct_HasFields`
   - `JobUnregisteredEvent_RecordStruct_HasFields`
   - `JobPausedEvent_RecordStruct_HasFields`
   - `JobResumedEvent_RecordStruct_HasFields`
   - `SchedulerFaultedEvent_RecordStruct_HasFields`
   - All events implement `IEquatable<T>` (via record semantics)
   - All events are `readonly record struct` types (no heap allocation by construction — verified by type shape, not a benchmark gate)

2. **[GREEN]** `src/Bifrost.Scheduling.Core/Events/*.cs` — one `readonly record struct` per event

3. **[REFACTOR]** XML docs; consider whether events should be a discriminated union or flat types (flat types match existing Bifrost event-stream pattern; go flat)

**Dependencies:** Task 1
**Parallelizable:** Yes

---

## Group E: InMemoryScheduleStore

### Task 18: InMemoryScheduleStore — basic CRUD
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Implements:** DR-5

1. **[RED]** `src/Bifrost.Tests.Scheduling/Stores/InMemoryScheduleStoreTests.cs`
   - `LoadAllAsync_Empty_ReturnsEmptyList`
   - `SaveAsync_ThenLoadAllAsync_ReturnsSavedJob`
   - `SaveAsync_DuplicateName_OverwritesExisting`
   - `DeleteAsync_ExistingJob_Removed`
   - `DeleteAsync_NonExistent_NoOp`
   - `RecordFiredAsync_UpdatesLastFiredAndNextFireAt`
   - `RecordFiredAsync_NonExistent_NoOp`
   - `ConcurrentSave_ThreadSafe` — parallel `SaveAsync` from 100 tasks, all jobs present after

2. **[GREEN]** `src/Bifrost.Scheduling/Stores/InMemoryScheduleStore.cs`
   - Internal `ConcurrentDictionary<string, JobRecord>`
   - All methods return completed `ValueTask`

3. **[REFACTOR]** XML docs noting "in-memory, single-instance only; state lost on restart"

**Dependencies:** Tasks 6, 7
**Parallelizable:** Yes

---

## Group F: Registry Implementation

### Task 19: ScheduleRegistry — core register/unregister
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Implements:** DR-1

1. **[RED]** `src/Bifrost.Tests.Scheduling/Registry/ScheduleRegistryRegisterTests.cs`
   - `RegisterAsync_NewJob_AddedToRegistry_AndPersistedToStore`
   - `RegisterAsync_DuplicateName_Throws`
   - `RegisterAsync_InvalidName_EmptyString_Throws`
   - `RegisterAsync_InvalidName_UpperCaseLetters_Throws` — regex `^[a-z0-9][a-z0-9-_.]{0,127}$`
   - `RegisterAsync_InvalidName_TooLong_Throws` — >128 chars
   - `RegisterAsync_NameStartingWithHyphen_Throws`
   - `UnregisterAsync_Existing_Removed_AndDeletedFromStore`
   - `UnregisterAsync_NonExistent_ReturnsFalse`
   - `RegisterAsync_PersistsToStore` — verify `IScheduleStore.SaveAsync` called
   - `RegisterAsync_StoreThrows_RollsBackRegistration` — DR-10 store failure case

2. **[GREEN]** `src/Bifrost.Scheduling/Registry/ScheduleRegistry.cs`
   - Internal `ConcurrentDictionary<string, JobRecord>`
   - Name validation via static regex
   - Register calls `store.SaveAsync` first, then adds to in-memory; rollback on store failure
   - Command-channel integration added in Task 24 (tick loop wake-up)

3. **[REFACTOR]** Extract name validation helper

**Dependencies:** Tasks 15, 18
**Parallelizable:** No (other registry tasks depend)

---

### Task 20: ScheduleRegistry — pause, resume, trigger
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Implements:** DR-1

1. **[RED]** `src/Bifrost.Tests.Scheduling/Registry/ScheduleRegistryControlTests.cs`
   - `PauseAsync_Running_TransitionsToPaused_AndPersists`
   - `PauseAsync_AlreadyPaused_NoOp`
   - `PauseAsync_NonExistent_Throws` — `JobNotFoundException`
   - `ResumeAsync_Paused_TransitionsToRunning_AndPersists`
   - `ResumeAsync_AlreadyRunning_NoOp`
   - `TriggerAsync_Existing_EmitsTriggerCommand` — verify command channel receives entry (requires mock of command router, since tick loop is Task 24)
   - `TriggerAsync_NonExistent_Throws`

2. **[GREEN]**
   - Pause/Resume mutate `State` field in registry and call `store.SaveAsync`
   - Trigger posts to internal `Channel<RegistryCommand>` (stubbed for now; tick loop connects in Task 24)
   - Add `JobNotFoundException` to `Bifrost.Scheduling.Core`

3. **[REFACTOR]** XML docs

**Dependencies:** Task 19
**Parallelizable:** No

---

### Task 21: ScheduleRegistry — query APIs
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** unit
**Implements:** DR-1

1. **[RED]** `src/Bifrost.Tests.Scheduling/Registry/ScheduleRegistryQueryTests.cs`
   - `GetJobs_Empty_ReturnsEmptyList`
   - `GetJobs_MultipleJobs_ReturnsAllSortedByName`
   - `GetJob_Existing_ReturnsDescriptor`
   - `GetJob_NonExistent_ReturnsNull`
   - `GetJob_ReturnsSnapshot_NotLive` — mutating registry after GetJob doesn't affect returned descriptor

2. **[GREEN]** Query methods return `JobDescriptor` snapshots

3. **[REFACTOR]** Ensure allocation discipline — `GetJobs` allocates once per call, not per-job

**Dependencies:** Task 19
**Parallelizable:** Yes (with Task 20)

---

## Group G: Dispatch Router + Dispatchers

### Task 22: IJobDispatcherRouter + routing by DispatchKind
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Implements:** DR-4

1. **[RED]** `src/Bifrost.Tests.Scheduling/Dispatch/JobDispatcherRouterTests.cs`
   - `DispatchAsync_OrchestratorKind_InvokesOrchestratorDispatcher`
   - `DispatchAsync_InlineKind_InvokesInlineDispatcher`
   - `DispatchAsync_CustomKind_ResolvesDispatcherFromDI`
   - `DispatchAsync_UnknownKind_Throws`
   - `DispatchAsync_DispatcherThrows_BubblesException` — error handling delegated to caller (tick loop)

2. **[GREEN]**
   - `src/Bifrost.Scheduling/Dispatch/IJobDispatcherRouter.cs` (internal)
   - `src/Bifrost.Scheduling/Dispatch/JobDispatcherRouter.cs`
   - Uses `IServiceProvider` to resolve by registered key

3. **[REFACTOR]** XML docs

**Dependencies:** Task 16
**Parallelizable:** Yes

---

### Task 23: OrchestratorJobDispatcher
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Implements:** DR-4

1. **[RED]** `src/Bifrost.Tests.Scheduling/Dispatch/OrchestratorJobDispatcherTests.cs`
   - `DispatchAsync_CallsFireFuncToProduceWork`
   - `DispatchAsync_CallsOrchestratorEnqueueAsync`
   - `DispatchAsync_FireFuncThrows_BubblesException`
   - `DispatchAsync_OrchestratorEnqueueThrows_BubblesException`
   - `DispatchAsync_FireFuncReceivesJobFireContext` — verify `FireTime` propagated

2. **[GREEN]** `src/Bifrost.Scheduling/Dispatch/OrchestratorJobDispatcher.cs`
   - Generic over `TWork`
   - Takes `Func<JobFireContext, TWork>` fire function and `IWorkOrchestrator<TWork>`
   - Implements `IJobDispatcher`

3. **[REFACTOR]** XML docs

**Dependencies:** Tasks 16, 22
**Parallelizable:** Yes

---

### Task 24: InlineJobDispatcher
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Implements:** DR-4

1. **[RED]** `src/Bifrost.Tests.Scheduling/Dispatch/InlineJobDispatcherTests.cs`
   - `DispatchAsync_InvokesDelegate`
   - `DispatchAsync_RunsOnPoolThread_NotCaller` — verify `ManagedThreadId` differs from caller (spin until delegate completes)
   - `DispatchAsync_DelegateThrows_BubblesException`
   - `DispatchAsync_CancellationToken_PropagatesToDelegate`
   - `DispatchAsync_PassesJobFireContextToDelegate`

2. **[GREEN]** `src/Bifrost.Scheduling/Dispatch/InlineJobDispatcher.cs`
   - Takes `Func<JobFireContext, CancellationToken, ValueTask>`
   - `DispatchAsync` does `Task.Run(() => delegate(...))` or equivalent to guarantee pool-thread execution

3. **[REFACTOR]** Prefer `ValueTask.FromResult` paths where possible

**Dependencies:** Tasks 16, 22
**Parallelizable:** Yes

---

## Group H: Tick Engine

### Task 25: ScheduleTickLoop — basic fire loop with TimeProvider
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Acceptance Test Ref:** (this is an acceptance-anchor task)
**Implements:** DR-7

1. **[RED]** `src/Bifrost.Tests.Scheduling/TickEngine/ScheduleTickLoopBasicTests.cs` (uses `FakeTimeProvider`)
   - `TickLoop_NoJobs_DoesNotFire` — advance 1 hour, no dispatches
   - `TickLoop_OneIntervalJob_FiresAtInterval` — 5-minute interval, advance 5 minutes, 1 dispatch
   - `TickLoop_OneIntervalJob_FiresMultipleTimes` — advance 15 minutes, 3 dispatches
   - `TickLoop_OneShotJob_FiresOnce_ThenRemovedFromHeap`
   - `TickLoop_MultipleJobs_FireInOrderOfNextFire`
   - `TickLoop_PausedJob_DoesNotFire`
   - `TickLoop_ResumedJob_Fires`
   - `TickLoop_TriggerCommand_FiresImmediately_RegardlessOfSchedule`
   - All tests use harness `AdvanceAsync` + `WaitForIdleAsync` — no `Thread.Sleep`

2. **[GREEN]** `src/Bifrost.Scheduling/TickEngine/ScheduleTickLoop.cs`
   - `BackgroundService` subclass
   - Internal `PriorityQueue<JobHandle, DateTimeOffset>`
   - `Channel<RegistryCommand>` for wake-up
   - `ExecuteAsync` main loop: compute delay, `Task.Delay(delay, timeProvider, ct)` or `WaitToReadAsync` on command channel (whichever first), drain commands, dispatch due
   - Calls `IJobDispatcherRouter.DispatchAsync` via fire-and-forget; calls `IScheduleStore.RecordFiredAsync`
   - Seeds from `IScheduleStore.LoadAllAsync` on startup

3. **[REFACTOR]** Extract `WaitForNextFireOrCommand` helper

**testingStrategy:** `characterizationRequired: false, propertyTests: false, benchmarks: false`

**Dependencies:** Tasks 14, 19, 20, 21, 22, 23, 24
**Parallelizable:** No (central piece; later tests build on it)

---

### Task 26: ScheduleTickLoop — missed-fire policy on startup
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Acceptance Test Ref:** Task 25
**Implements:** DR-3, DR-7, DR-10

1. **[RED]** `src/Bifrost.Tests.Scheduling/TickEngine/MissedFireStartupTests.cs`
   - `Startup_CoalescePolicy_OneCatchUpFire` — Setup: store returns job with `lastFiredAt = now - 5min`, interval 1min, policy Coalesce. Assert: 1 dispatch on startup, next fire at now+1min.
   - `Startup_FireAllMissedPolicy_FiresAllMissed` — Same setup, FireAllMissed. Assert: 5 dispatches, then normal schedule.
   - `Startup_SkipMissedPolicy_SkipsAll` — Same setup, SkipMissed. Assert: 0 dispatches, next fire at now+1min.
   - `Startup_FireAllMissed_ExceedsCap_CapsAtMax` — 500 missed fires, fires 100, logs warning
   - `Startup_NoMissedFires_NormalSchedule`

2. **[GREEN]** In `ScheduleTickLoop.ExecuteAsync` startup sequence:
   - After `LoadAllAsync`, for each job call `MissedFirePolicyApplier.ComputeMissedFires(...)`
   - Enqueue each missed fire time as an immediate fire command
   - Then compute normal `NextFireAt` via `ComputeNextFire(now, now)`

3. **[REFACTOR]** Extract `ApplyStartupMissedFirePolicies` method

**Dependencies:** Task 25
**Parallelizable:** No

---

### Task 27: ScheduleTickLoop — always-leader v1 behavior and multi-instance warning
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Acceptance Test Ref:** Task 25
**Implements:** DR-6

> The tick loop in v1 is **unconditionally always-leader** (DR-6/R4). There is no leadership
> acquisition, lease, or coordination code path — multi-instance coordination arrives with the
> Marten adapter, contract and all.

1. **[RED]** `src/Bifrost.Tests.Scheduling/TickEngine/SingleProcessBehaviorTests.cs`
   - `TickLoop_SingleProcess_TicksEveryRegisteredJob` — default in-memory store, loop ticks all jobs unconditionally
   - `TickLoop_MultiInstanceExpected_LogsStartupWarning` — `SchedulerOptions.MultiInstanceExpected = true` with a non-exclusive-capable store: startup log contains a prominent warning that duplicate fires will occur
   - `TickLoop_MultiInstanceNotExpected_NoWarning` — default options, no warning logged

2. **[GREEN]**
   - No leadership code path: the loop ticks unconditionally
   - Add `SchedulerOptions.MultiInstanceExpected` (default `false`); on startup, when `true`, emit a prominent warning log that v1 scheduling is single-process and duplicate fires occur across instances (auto-detection is out of scope — see Deferred Items)

3. **[REFACTOR]** Extract startup-validation/warning helper; XML docs stating the DR-6 documentation contract: "Run Bifrost scheduling on exactly one process. Multi-instance support arrives with the storage adapter (e.g., `Bifrost.Scheduling.Marten`)."

**Dependencies:** Task 26
**Parallelizable:** No

---

### Task 28: ScheduleTickLoop — graceful shutdown
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Acceptance Test Ref:** Task 25
**Implements:** DR-10

1. **[RED]** `src/Bifrost.Tests.Scheduling/TickEngine/GracefulShutdownTests.cs`
   - `Shutdown_NoInFlightDispatches_CompletesImmediately`
   - `Shutdown_InFlightDispatches_WaitsForCompletion_WithinTimeout`
   - `Shutdown_InFlightDispatchExceedsTimeout_Abandoned` — loop returns after timeout expires even if dispatches pending
   - `Shutdown_AfterStop_NoNewFires` — calling `StopAsync` then advancing time: no dispatches
   - `Shutdown_StopAsyncIdempotent_AndStopBeforeStart_NoThrow` — `StopAsync` twice, and stop-before-fully-started, never throws from disposed primitives (NCronJob#172)

2. **[GREEN]**
   - Override `StopAsync` in `ScheduleTickLoop`
   - Track in-flight dispatches via `CountdownEvent` or similar
   - Wait up to `SchedulerOptions.ShutdownTimeout`
   - Guard stop path for idempotency and stop-before-start

3. **[REFACTOR]** XML docs

**Dependencies:** Task 27
**Parallelizable:** No

---

### Task 29: ScheduleTickLoop — tick loop exception recovery
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Acceptance Test Ref:** Task 25
**Implements:** DR-10

1. **[RED]** `src/Bifrost.Tests.Scheduling/TickEngine/TickLoopExceptionTests.cs`
   - `TickLoop_Exception_LogsError_AndEmitsSchedulerFaultedEvent`
   - `TickLoop_Exception_RestartsLoop`
   - `TickLoop_3ConsecutiveRestartFailuresWithin60s_TransitionsToFaulted`
   - `TickLoop_Faulted_HealthCheckReportsUnhealthy` — integration with Task 33
   - `TickLoop_ClockSkew_LogsWarning_Continues` — `TimeProvider` returns earlier value than previous reading

2. **[GREEN]**
   - Wrap main loop body in try/catch
   - Count restart attempts in 60-second sliding window
   - Emit `SchedulerFaultedEvent` on transition

3. **[REFACTOR]** Extract `RestartPolicy` helper

**Dependencies:** Task 28
**Parallelizable:** No

---

### Task 30: ScheduleTickLoop — dispatch failure isolation
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Acceptance Test Ref:** Task 25
**Implements:** DR-4, DR-10

1. **[RED]** `src/Bifrost.Tests.Scheduling/TickEngine/DispatchFailureTests.cs`
   - `DispatchFailure_EmitsJobFireFailedEvent`
   - `DispatchFailure_DoesNotCrashTickLoop` — next job fires normally
   - `DispatchFailure_NextFireTimeStillComputed` — job stays scheduled
   - `DispatchFailure_StoreCheckpointFails_Logged_ButNextFireStillScheduled` — DR-10 checkpoint-failure case

2. **[GREEN]**
   - Wrap dispatch call in try/catch
   - Emit `JobFireFailedEvent` on exception
   - Continue scheduling next fire regardless

3. **[REFACTOR]** Extract `SafeDispatch` helper

**Dependencies:** Task 29
**Parallelizable:** No

---

## Group I: Observability — metrics, events, and health checks

Implements **DR-8: Observability — metrics, events, and health checks**. Metrics via `System.Diagnostics.Metrics`, scheduler event publication through the Bifrost event stream, `IHealthCheck`, and the read-only `IBifrostScheduleInspector` API.

### Task 31: Observability — Scheduler metrics (System.Diagnostics.Metrics)
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Implements:** DR-8

1. **[RED]** `src/Bifrost.Tests.Scheduling/Observability/SchedulerMetricsTests.cs`
   - `Meter_Name_IsBifrostScheduling`
   - `JobsRegistered_Counter_IncrementsOnRegister`
   - `JobsRegistered_Counter_DecrementsOnUnregister`
   - `JobsFired_Counter_IncrementsOnSuccessfulDispatch_WithJobNameTag`
   - `FireLatency_Histogram_RecordsLatencyBetweenNextFireAndDispatch`
   - `MissedFires_Counter_IncrementsWithPolicyTag`
   - `DispatchFailures_Counter_IncrementsWithJobNameAndExceptionTypeTags`
   - Uses `MetricCollector<T>` from `Microsoft.Extensions.Diagnostics.Testing`

2. **[GREEN]** `src/Bifrost.Scheduling/Observability/SchedulerMetrics.cs`
   - Internal class with `Meter` instance and all instruments
   - Wired into `ScheduleRegistry` and `ScheduleTickLoop`
   - Add `Microsoft.Extensions.Diagnostics.Testing` to test project

3. **[REFACTOR]** Extract meter name constant; use `InstrumentationConstants`

**Dependencies:** Task 30
**Parallelizable:** Yes (after Task 30)

---

### Task 32: Observability — Scheduler event publication
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Implements:** DR-8

1. **[RED]** `src/Bifrost.Tests.Scheduling/Observability/SchedulerEventsPublicationTests.cs`
   - `OnJobRegistered_PublishesJobRegisteredEvent`
   - `OnJobUnregistered_PublishesJobUnregisteredEvent`
   - `OnJobPaused_PublishesJobPausedEvent`
   - `OnJobResumed_PublishesJobResumedEvent`
   - `OnFireSuccess_PublishesJobFiredEvent`
   - `OnFireFailure_PublishesJobFireFailedEvent`
   - `OnMissedFireApplied_PublishesJobMissedFireEvent`
   - `OnFaulted_PublishesSchedulerFaultedEvent`

2. **[GREEN]**
   - Add `ISchedulerEventSink` abstraction (or reuse existing event-stream infrastructure from `Bifrost` package)
   - Wire all event-emitting points in registry and tick loop

3. **[REFACTOR]** XML docs

**Dependencies:** Tasks 17, 31
**Parallelizable:** Yes (with 31)

---

### Task 33: Observability — SchedulerHealthCheck
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Implements:** DR-8

1. **[RED]** `src/Bifrost.Tests.Scheduling/Observability/SchedulerHealthCheckTests.cs`
   - `HealthCheck_Running_ReportsHealthy`
   - `HealthCheck_NoTickInThreeIntervals_ReportsUnhealthy` — FakeTimeProvider advances without tick
   - `HealthCheck_DispatchFailureRateAbove50Percent_ReportsUnhealthy` — last 100 fires have >50 failures
   - `HealthCheck_Faulted_ReportsUnhealthy` — from Task 29

2. **[GREEN]** `src/Bifrost.Scheduling/Observability/SchedulerHealthCheck.cs`
   - Implements `IHealthCheck`
   - Uses `ITickHealthMonitor` (internal) tracking last-tick timestamp + rolling failure window
   - Registered via `AddHealthChecks().AddCheck<SchedulerHealthCheck>("bifrost.scheduling")`

3. **[REFACTOR]** Extract failure-rate computation

**Dependencies:** Task 32
**Parallelizable:** Yes (with 31, 32)

---

### Task 34: Observability — IBifrostScheduleInspector
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** unit
**Implements:** DR-8

1. **[RED]** `src/Bifrost.Tests.Scheduling/Observability/ScheduleInspectorTests.cs`
   - `GetJobs_DelegatesToRegistry`
   - `GetJob_DelegatesToRegistry`
   - `GetMetricsSnapshot_ReturnsCurrentValues` — job count, fires in last minute, etc.
   - `Inspector_IsReadOnly_NoMutationMethods` — via reflection
   - (`GetNextOccurrences(name, count)` is added in Task 51 — R10/DR-8: previews must be computed by the same cadence engine that fires)

2. **[GREEN]**
   - `src/Bifrost.Scheduling.Core/IBifrostScheduleInspector.cs`
   - `src/Bifrost.Scheduling/Observability/ScheduleInspector.cs`

3. **[REFACTOR]** XML docs

**Dependencies:** Tasks 21, 31
**Parallelizable:** Yes

---

## Group J: Testing Primitives

### Task 35: ISchedulerTestHarness contract
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** unit
**Implements:** DR-9

1. **[RED]** `src/Bifrost.Tests.Scheduling/Testing/ISchedulerTestHarnessTests.cs`
   - `ISchedulerTestHarness_HasAdvanceAsyncMethod`
   - `ISchedulerTestHarness_HasFireDueJobsAsyncMethod`
   - `ISchedulerTestHarness_HasWaitForIdleAsyncMethod`

2. **[GREEN]** `src/Bifrost.Scheduling.Testing/ISchedulerTestHarness.cs` (public)

3. **[REFACTOR]** XML docs

**Dependencies:** Task 3
**Parallelizable:** Yes

---

### Task 36: SchedulerTestHarness implementation
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Implements:** DR-9

1. **[RED]** `src/Bifrost.Tests.Scheduling/Testing/SchedulerTestHarnessTests.cs`
   - `AdvanceAsync_SingleJob_5Min_Fires_5Min_Interval_OnceDeterministically`
   - `AdvanceAsync_MultipleJobs_AllFireInOrder`
   - `AdvanceAsync_WaitsForInFlightDispatches_BeforeReturn`
   - `FireDueJobsAsync_OnlyFiresDueJobs_NotFutureJobs`
   - `WaitForIdleAsync_Timeout_Throws` — if loop never idles within timeout
   - `WaitForIdleAsync_TickLoopIdle_ReturnsImmediately`
   - `Harness_RequiresFakeTimeProvider` — throws if real `TimeProvider` injected
   - `Harness_AllDispatchModes_Deterministic` — orchestrator + inline + custom all drive to completion

2. **[GREEN]** `src/Bifrost.Scheduling.Testing/SchedulerTestHarness.cs`
   - Depends on `FakeTimeProvider` + access to `ScheduleTickLoop` internals (via `internal` + `InternalsVisibleTo`)
   - `AdvanceAsync(duration)` — advance fake clock, then await `WaitForIdleAsync`
   - `FireDueJobsAsync()` — send special `ForceFireDueNow` command
   - `WaitForIdleAsync(timeout)` — poll loop state

3. **[REFACTOR]** Extract `DispatchTracker` for in-flight counting

**Dependencies:** Tasks 25, 35
**Parallelizable:** No

---

### Task 37: AddSchedulerTesting DI extension
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Implements:** DR-9

1. **[RED]** `src/Bifrost.Tests.Scheduling/Testing/AddSchedulerTestingTests.cs`
   - `AddSchedulerTesting_RegistersFakeTimeProvider`
   - `AddSchedulerTesting_RegistersISchedulerTestHarness`
   - `AddSchedulerTesting_CompatibleWithAddScheduler` — combined setup works end-to-end

2. **[GREEN]** `src/Bifrost.Scheduling.Testing/DependencyInjection/SchedulerTestingExtensions.cs`
   - `IServiceCollection.AddSchedulerTesting()` — replaces `TimeProvider` with `FakeTimeProvider` and registers harness

3. **[REFACTOR]** XML docs

**Dependencies:** Task 36
**Parallelizable:** No

---

## Group K: DI Extensions, Fluent Builder, Integration

### Task 38: Fluent builder — IJobBuilder + cadence DSL
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Implements:** DR-1, DR-2, DR-3, DR-4

1. **[RED]** `src/Bifrost.Tests.Scheduling/DependencyInjection/FluentBuilderTests.cs`
   - `AddJob_ByWorkType_ReturnsJobBuilder`
   - `Every_Interval_SetsIntervalCadence`
   - `Cron_Expression_SetsCronCadence`
   - `At_DateTimeOffset_SetsOneShotCadence`
   - `After_TimeSpan_SetsRelativeOneShotCadence` — resolved to an absolute `OneShotCadence` by the registry at registration time (R1; Task 45)
   - `WithJitter_Fraction_UpdatesIntervalCadence`
   - `WithMissedFirePolicy_Policy_SetsPolicy`
   - `DispatchTo_OrchestratorType_SetsOrchestratorDispatch` — chained `.DispatchTo<IWorkOrchestrator<TWork>>(fire)`
   - `AddInlineJob_WithRunDelegate_SetsInlineDispatch`
   - `DispatchVia_CustomType_SetsCustomDispatch`
   - `Builder_BuildsJobRecord_OnRegistration`

2. **[GREEN]** `src/Bifrost.Scheduling/DependencyInjection/`
   - `ISchedulerBuilder.cs` (public)
   - `SchedulerBuilder.cs` (internal)
   - `IJobBuilder<TWork>.cs` / `JobBuilder<TWork>.cs`
   - `IInlineJobBuilder.cs` / `InlineJobBuilder.cs`
   - `IJobBuilderExtensions.cs` — fluent cadence and dispatch chain

3. **[REFACTOR]** Extract cadence-factory helpers

**Dependencies:** Tasks 19, 22, 23, 24
**Parallelizable:** No

---

### Task 39: AddScheduler DI extension + UseStore<T>
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Implements:** DR-1, DR-5, DR-6

1. **[RED]** `src/Bifrost.Tests.Scheduling/DependencyInjection/AddSchedulerTests.cs`
   - `AddScheduler_RegistersIScheduleStore_InMemoryDefault`
   - `AddScheduler_RegistersIScheduleRegistry`
   - `AddScheduler_RegistersTickLoopAsHostedService`
   - `AddScheduler_RegistersHealthCheck`
   - `AddScheduler_WithJobs_JobsPresentAtStartup`
   - `UseStore_CustomStore_ReplacesInMemory`
   - `UseStore_MultipleCalls_LastWins`
   - `AddScheduler_RegistersTimeProvider_DefaultIfNotPresent`

2. **[GREEN]** `src/Bifrost.Scheduling/DependencyInjection/SchedulerServiceCollectionExtensions.cs`
   - `IServiceCollection.AddScheduler(Action<ISchedulerBuilder>?)`
   - Registers all scheduler internals
   - Builder completes by calling `RegisterAsync` for each DI-time job during the first tick

3. **[REFACTOR]** Extract default-registration constants

**Dependencies:** Tasks 18, 19, 25, 33, 38
**Parallelizable:** No

---

### Task 40: Integration — scheduler + orchestrator + resilience + DLQ
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** acceptance
**Implements:** DR-4 (integration across feature)

1. **[RED]** `src/Bifrost.Tests.Scheduling/Integration/SchedulerOrchestratorDlqTests.cs`
   - `ScheduledJob_DispatchesToOrchestrator_WorkProcessedByHandler`
   - `ScheduledJob_HandlerThrows_ResilienceRetries_ThenDeadLetters`
   - `ScheduledJob_KeepsFiring_EvenAfterHandlerFailures` — schedule stays alive
   - `ScheduledJob_EventStream_PublishesJobFiredAndWorkCompleted` — verify both event streams emit
   - Uses real `InMemoryScheduleStore` + real `WorkOrchestrator<TWork>` + real `WithResilience()` + real `WithDeadLetterQueue()`

2. **[GREEN]** No new production code — this validates integration across existing pieces

3. **[REFACTOR]** N/A

**Dependencies:** Task 39
**Parallelizable:** No

---

## Group L: Benchmarks — Performance targets (revised per R2)

Implements **DR-11: Performance targets (revised per R2)**. Merge-gating validation covers the
correctness-flavored targets only: saturation correctness (1,000+ simultaneous due jobs, none lost —
Task 48), p99 fire latency < 5ms at 10K registered jobs, and the O(log n) per-fire scaling curve.
Allocation figures (dispatch-handoff steady state approaching 0 B/fire; registration < 256 B) are
**tracked benchmark targets** — regressions are investigated, but they are not acceptance gates and
do not fail the build or block merge.

### Task 41: Benchmarks — Performance targets (registry operations)
**Phase:** Benchmark
**Test Layer:** benchmark
**Implements:** DR-11

**Steps:**
1. Create `src/Bifrost.Benchmarks/Scheduling/` directory
2. `RegistryRegistrationBenchmarks.cs` — `[MemoryDiagnoser]`
   - `Register_Single` — register one job, measure allocation and time
   - `Register_Bulk` — register 1000 jobs
   - `Pause_Single`, `Resume_Single`, `Trigger_Single`
   - `GetJobs_1000Jobs` — allocations tracked (benchmark target: a single result-list allocation; registration target < 256 B per DR-11 — tracked, not gating)
3. Add `Bifrost.Benchmarks.csproj` ProjectReference to `Bifrost.Scheduling.csproj`

**Verification:**
- Benchmarks run via `dotnet run -c Release --project src/Bifrost.Benchmarks -- --job Dry -f "*Scheduling*"`

**testingStrategy:** `benchmarks: true`

**Dependencies:** Task 39
**Parallelizable:** Yes

---

### Task 42: Benchmarks — Performance targets (tick engine)
**Phase:** Benchmark
**Test Layer:** benchmark
**Implements:** DR-7, DR-11

**Steps:**
1. `TickEngineBenchmarks.cs` — `[MemoryDiagnoser]`, `[Params(10, 100, 1000, 10000)] public int JobCount`
   - `FireLatency` — measure p99 from `NextFireAt` to dispatch handoff (**merge gate:** p99 < 5ms at 10K jobs, in-memory store)
   - `ScalingCurve` — per-fire cost across the `JobCount` params (**merge gate:** O(log n) heap behavior, verified by the scaling curve)
   - `SteadyStateAllocation` — dispatch-handoff allocations per fire (**tracked benchmark target, not a gate:** approaching 0 B/fire; per design R2, a truly allocation-free loop requires a reusable wake primitive + pooled dispatch state and may be deferred to a follow-up optimization pass without changing the public API)
2. Note: IterationSetup for async work per Bifrost memory rules — use `void` with `.AsTask().GetAwaiter().GetResult()`

**Verification:**
- Benchmark runs; p99 fire latency and O(log n) scaling validated (merge-gating); allocation figures recorded as tracked targets

**testingStrategy:** `benchmarks: true`

**Dependencies:** Task 39
**Parallelizable:** Yes

---

### Task 43: Benchmarks — Performance targets (cadence compute)
**Phase:** Benchmark
**Test Layer:** benchmark
**Implements:** DR-2, DR-11

**Steps:**
1. `CadenceComputeBenchmarks.cs` — `[MemoryDiagnoser]`
   - `Interval_ComputeNextFire`
   - `IntervalWithJitter_ComputeNextFire`
   - `Cron_ComputeNextFire_EveryMinute`
   - `Cron_ComputeNextFire_ComplexExpression`
   - `OneShot_ComputeNextFire`

**testingStrategy:** `benchmarks: true`

**Dependencies:** Task 2, Tasks 9–13
**Parallelizable:** Yes

---

### Task 44: Benchmarks — Performance targets (dispatch overhead)
**Phase:** Benchmark
**Test Layer:** benchmark
**Implements:** DR-4, DR-11

**Steps:**
1. `DispatchBenchmarks.cs` — `[MemoryDiagnoser]`
   - `OrchestratorDispatch_Overhead` — measure time/alloc between router receiving context and orchestrator receiving work
   - `InlineDispatch_Overhead` — same for inline
   - `CustomDispatch_Overhead` — user-provided IJobDispatcher

**testingStrategy:** `benchmarks: true`

**Dependencies:** Task 39
**Parallelizable:** Yes

---

## Group M: Design-Revision Tasks (2026-06-12 — R1–R10, DR-12, DR-13)

Added by the 2026-06-12 plan revision to match the revised design. Tests are TUnit on
Microsoft.Testing.Platform (`dotnet test --project src/Bifrost.Tests.Scheduling`); assertions MUST
be awaited. Tasks 45 and 46 can start as soon as their dependencies allow (they do not need to wait
for Group L).

### Task 45: RelativeOneShotCadence — registration-time resolution via TimeProvider (R1/DR-2)
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Implements:** DR-2

1. **[RED]** `src/Bifrost.Tests.Scheduling/Cadences/RelativeOneShotResolutionTests.cs`
   - `Cadence_After_PerformsNoClockAccess` — `Cadence.After(delay)` returns `RelativeOneShotCadence(delay)`; the static factory never reads any clock (no `TimeProvider`, no `DateTimeOffset.UtcNow`)
   - `RegisterAsync_RelativeOneShot_ResolvesToAbsoluteOneShot_ViaInjectedTimeProvider` — with `FakeTimeProvider` at `T0`, registering `Cadence.After(5.Minutes())` stores a `OneShotCadence` with `FireAt == T0 + 5min`
   - `RegisterAsync_Resolution_TracksFakeClock_NotSystemClock` — advance the fake clock to `T1` before registering; resolved `FireAt == T1 + delay` (proves resolution uses the registry's injected `TimeProvider`)
   - `RelativeOneShotCadence_ComputeNextFire_Throws` — an unresolved relative cadence must never reach the tick loop; `InvalidOperationException` with a descriptive message
   - Expected failure: registry stores the relative cadence unresolved / resolution path doesn't exist

2. **[GREEN]**
   - `ScheduleRegistry.RegisterAsync` (and the fluent-builder registration path, Task 39): when the cadence is `RelativeOneShotCadence`, resolve to `new OneShotCadence(timeProvider.GetUtcNow() + Delay)` before persisting the `JobRecord`
   - `RelativeOneShotCadence.ComputeNextFire` throws `InvalidOperationException`

3. **[REFACTOR]** XML docs on `Cadence.After` stating the R1 contract: "resolved against the registry's injected `TimeProvider` at registration time; the static factory performs no clock access"

**Dependencies:** Tasks 9, 19
**Parallelizable:** Yes

---

### Task 46: Banned-API architecture test (R1/DR-7)
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** unit (architecture)
**Implements:** DR-7

1. **[RED]** `src/Bifrost.Tests.Scheduling/Architecture/BannedTimeApiTests.cs`
   - `SchedulingAssemblies_ContainNoBannedClockApis` — scans the shipped `Bifrost.Scheduling.Core` and `Bifrost.Scheduling` assemblies (IL member references) for `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now`, `DateTimeOffset.UtcNow`
   - `SchedulingAssemblies_ContainNoNonTimeProviderTaskDelay` — every `Task.Delay` reference must be a `TimeProvider`-accepting overload
   - `BannedApiCheck_CatchesDeliberateViolation` — self-test: the scanner flags a test-local fixture method that calls `DateTime.UtcNow` (proves the check isn't vacuously green)
   - Expected failure: scanner doesn't exist (and any existing violation in shipping code surfaces here)

2. **[GREEN]**
   - Implement the IL/member-reference scanner in the architecture test
   - Additionally make the check **build-failing** at compile time: add `Microsoft.CodeAnalysis.BannedApiAnalyzers` + a `BannedSymbols.txt` (banning `DateTime.Now`/`DateTime.UtcNow`/`DateTimeOffset.Now`/`DateTimeOffset.UtcNow` and the non-`TimeProvider` `Task.Delay` overloads) to `Bifrost.Scheduling.Core` and `Bifrost.Scheduling` (analyzer version pinned in `src/Directory.Packages.props` per CPM)
   - Fix any violations the check surfaces

3. **[REFACTOR]** Document the banned-API rule in both csproj files (comment) and the package README section; keep `BannedSymbols.txt` shared via a single linked file

**Dependencies:** Tasks 2, 4
**Parallelizable:** Yes

---

### Task 47: Early-wake clamp (R2/DR-7)
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Acceptance Test Ref:** Task 25
**Implements:** DR-7

1. **[RED]** `src/Bifrost.Tests.Scheduling/TickEngine/EarlyWakeClampTests.cs` (uses `FakeTimeProvider`)
   - `TickLoop_WakesBeforeNextFireAt_ReSleeps_NoDispatch` — loop woken (command-channel nudge) while `now < NextFireAt`: zero dispatches, loop re-arms until `NextFireAt <= now`
   - `TickLoop_TimerFires20msEarly_ExactlyOneDispatchPerOccurrence` — simulate the timer completing up to 20ms before the scheduled instant (NCronJob#327 class): exactly one dispatch per occurrence, never a duplicate
   - `TickLoop_NextFire_ComputedFromScheduledOccurrenceTime` — after a fire, the next fire equals `ComputeNextFire(lastFiredAt: scheduledOccurrence, …)` — never computed from a wall-clock reading earlier than the scheduled instant

2. **[GREEN]** In `ScheduleTickLoop`:
   - After any wake, if `_heap` top is not yet due (`now < NextFireAt`), recompute the delay and continue waiting — dispatch only when `NextFireAt <= now`
   - In `DispatchDueJobs`, pass the **scheduled occurrence time** (the heap key), not `now`, as `lastFiredAt` into `ComputeNextFire`

3. **[REFACTOR]** Extract clamp logic into the `WaitForNextFireOrCommand` helper from Task 25; XML doc the invariant

**Dependencies:** Task 25
**Parallelizable:** No (tick-engine internals)

---

### Task 48: DR-10 hardening — clock jumps, failure isolation, DST properties, resume pin, saturation (R9)
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration + property
**Acceptance Test Ref:** Task 25
**Implements:** DR-10 (also gates DR-11 saturation correctness)

1. **[RED]**
   - `src/Bifrost.Tests.Scheduling/TickEngine/ClockJumpTests.cs`
     - `TickLoop_BackwardClockJump_ReArmsWithinOneWaitCycle` — `FakeTimeProvider` jumps backward by days mid-wait; the loop never sleeps on a stale absolute deadline and re-arms within one wait cycle (quartznet#1508/#2034: backward jump silently stopped ALL firing)
   - `src/Bifrost.Tests.Scheduling/TickEngine/JobFailureIsolationTests.cs`
     - `ComputeNextFire_Throws_JobMarkedFaulted_PublishesJobFireFailedEvent` — a cadence whose `ComputeNextFire` throws marks only that job `Faulted`
     - `ComputeNextFire_Throws_OtherJobsKeepFiring` — one faulted job never halts scheduling of others (Hangfire#530/#529/#537 crash-looped the shared loop)
   - `src/Bifrost.Tests.Scheduling/Cadences/CronCadenceDstPropertyTests.cs`
     - **Property test:** `CronCadence_ComputeNextFire_StrictlyGreaterThanInput_AcrossDstWeek` — `ComputeNextFire(t) > t` for every instant across both DST boundaries (transition week), in multiple zones **including half-hour offsets** (e.g., `Australia/Adelaide`, `Asia/Tehran`) — quartznet#2497/#332 infinite-loop class
     - `CronCadence_FallBackRepeatedHour_PerMinute_FiresExactly60Times` — per-minute cron across the fall-back repeated hour: exactly 60 fires, never a tight refire loop (quartznet#2475) and never a one-hour silence (Hangfire#567)
   - `src/Bifrost.Tests.Scheduling/TickEngine/ResumePinTests.cs`
     - `Resume_AfterNMissedOccurrences_FiresNothing_NextFireIsNaturalOccurrence` — **pin test**: paused across N occurrences → resume → zero catch-up fires; missed-fire policies apply only on startup recovery from a store
   - `src/Bifrost.Tests.Scheduling/TickEngine/SaturationTests.cs`
     - `Saturation_1000PlusJobsDueSameInstant_AllDispatched_NoneLost` — 1,000+ jobs due at the same instant are all dispatched (or carried into the immediately following loop iterations); none silently lost (Hangfire#751)

2. **[GREEN]**
   - Wait computation re-reads `TimeProvider.GetUtcNow()` per cycle and clamps negative/oversized delays (backward-jump re-arm); log a warning on non-monotonic readings (extends Task 29's clock-skew handling)
   - Wrap per-job `ComputeNextFire` in try/catch: mark `Faulted`, publish `JobFireFailedEvent`, continue the loop
   - Fix any DST defects the property tests surface in `CronCadence` (Cronos boundary handling)
   - Resume path schedules the next natural occurrence only (no missed-fire application — confirms Task 26's startup-only wiring)
   - `DispatchDueJobs` drains every due heap entry per iteration

3. **[REFACTOR]** Consolidate fault-isolation handling into a `SafeComputeNextFire` helper alongside Task 30's `SafeDispatch`

**testingStrategy:** `propertyTests: true`

**Dependencies:** Tasks 13, 26, 29, 47
**Parallelizable:** No (tick-engine internals)

---

### Task 49: DR-12 delivery semantics — scheduled FireTime + at-least-once documentation
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Implements:** DR-12

1. **[RED]** `src/Bifrost.Tests.Scheduling/Dispatch/DeliverySemanticsTests.cs`
   - `JobFireContext_FireTime_IsScheduledOccurrenceTime_NotWallClock` — fire a job whose dispatch happens after a (fake-clock) late wake: `FireTime` equals the scheduled occurrence instant, not the dispatch-time reading
   - `JobFireContext_FireTime_StableAcrossReFire` — simulate restart with a checkpoint missing for the last occurrence (crash between dispatch handoff and `RecordFiredAsync`); the `Coalesce` re-fire presents the **identical** `(JobName, FireTime)` pair as the original fire
   - `TriggerAsync_FireTime_IsTriggerInstant` — on-demand triggers use the trigger instant (no scheduled occurrence exists)

2. **[GREEN]**
   - `ScheduleTickLoop.DispatchDueJobs` constructs `JobFireContext` with the scheduled occurrence time (the heap key / missed-occurrence time), never `_time.GetUtcNow()`
   - Missed-fire catch-up path carries the original occurrence time into the re-fire context

3. **[REFACTOR]** XML docs (blunt, per the industry precedents in the design):
   - `IJobDispatcher.DispatchAsync` and the inline-dispatch delegate (`IInlineJobBuilder.Run`): "Execution is **at-least-once per scheduled occurrence**. Your handler will sometimes run more than once for the same occurrence — make it idempotent. Use `(JobName, FireTime)` as the idempotency/dedup key."
   - `JobFireContext.FireTime`: "the scheduled occurrence time — stable across a re-fire of the same occurrence; never the wall-clock dispatch time"
   - Document the duplicate window precisely: a crash between dispatch handoff and the `RecordFiredAsync` checkpoint means the missed-fire policy re-fires the occurrence on restart (with a durable store); `Coalesce` (default) bounds duplicates to one catch-up fire

**Dependencies:** Tasks 16, 26, 47
**Parallelizable:** No (depends on tick-engine fire path)

---

### Task 50: DR-13 AOT validation — PublishAot smoke, trim-warnings-as-errors, no reflective activation
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** unit (architecture) + CI
**Implements:** DR-13

1. **[RED]** `src/Bifrost.Tests.Scheduling/Architecture/AotSafetyTests.cs`
   - `SchedulingAssemblies_ContainNoTypeGetTypeCalls` — IL member-reference scan of `Bifrost.Scheduling.Core` and `Bifrost.Scheduling`: no `Type.GetType`, no `Activator.CreateInstance(Type)` from strings, no `Expression.Compile`
   - `DispatcherTypeName_IsDiagnosticOnly` — the dispatch router resolves custom dispatchers via **generic DI registration** (`DispatchVia<TDispatcher>()` registers `TDispatcher` at configuration time); `JobRecord.DispatcherTypeName` is never an activation input
   - Expected failure: scanner doesn't exist (and any reflective activation surfaces here)

2. **[GREEN]**
   - Verify/enforce `IsAotCompatible=true` (repo standard) on `Bifrost.Scheduling.Core`, `Bifrost.Scheduling`, and `Bifrost.Scheduling.Testing`, with trim/AOT analyzer warnings flowing into warnings-as-errors (zero warnings)
   - Create `samples/Bifrost.Scheduling.AotSmoke/` — a minimal `PublishAot=true` console host exercising: DI-time + runtime registration, **all three dispatch modes** (orchestrator, inline, custom via `DispatchVia<TDispatcher>()`), and the in-memory store
   - Add a CI step publishing the smoke app with `dotnet publish -c Release /p:PublishAot=true` and running it (non-zero exit on failure)

3. **[REFACTOR]** README/package-description phrasing uses only the defensible comparative claims from the design (DR-13): "no reflection-based job activation, no type-name serialization, no expression-tree compilation" — never "first" or "only"

**Dependencies:** Tasks 22, 39
**Parallelizable:** Yes (after Task 39)

---

### Task 51: R10 ergonomics — no silent fire-now, double-AddScheduler guard, GetNextOccurrences
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Implements:** DR-1, DR-8

1. **[RED]**
   - `src/Bifrost.Tests.Scheduling/Registry/RegistrationErgonomicsTests.cs`
     - `RegisterAsync_AtCadenceInPast_Throws` — `Cadence.At(past)` throws at registration; never a silent immediate fire (quartznet#636/#2180, Hangfire#1637)
     - `ReRegister_UnchangedSchedule_NeverFiresImmediately` — re-registering/updating a job with an unchanged schedule causes zero dispatches before the next legitimate occurrence (quartznet#1545)
     - `Update_FutureSchedule_NeverFiresImmediately` — updating to a future schedule never fires as a side effect; `TriggerAsync` is the only API that fires on demand
   - `src/Bifrost.Tests.Scheduling/DependencyInjection/DoubleAddSchedulerTests.cs`
     - `AddScheduler_CalledTwice_ThrowsInvalidOperationException` — registration APIs that look composable but silently no-op are a trap (NCronJob#138)
   - `src/Bifrost.Tests.Scheduling/Observability/NextOccurrencesTests.cs`
     - `GetNextOccurrences_ReturnsNFutureInstants_Ordered` — `IBifrostScheduleInspector.GetNextOccurrences(name, count)` returns `count` distinct, ordered future instants
     - `GetNextOccurrences_MatchesActualFires` — drive the scheduler with the test harness: the instants previewed are exactly the instants the tick engine fires (same cadence engine for display and firing — coravel#250, Hangfire#899)

2. **[GREEN]**
   - Registration-time validation in `ScheduleRegistry`: one-shot cadence with a past fire time throws a descriptive exception
   - Re-register/update path recomputes `NextFireAt` from the schedule only — no fire-now side effects
   - `AddScheduler` registers a marker service and throws `InvalidOperationException` if already present
   - Add `GetNextOccurrences(string name, int count)` to `IBifrostScheduleInspector` (Task 34); implement in `ScheduleInspector` by iterating the job's `Cadence.ComputeNextFire` — the same engine the tick loop uses

3. **[REFACTOR]** XML docs: "mutating operations never cause immediate execution as a side effect"; document IIS/App Service idle-timeout behavior alongside the health check (R10)

**Dependencies:** Tasks 19, 34, 36, 39
**Parallelizable:** Yes (after Task 39)

---

## Parallelization Strategy

### Sequential critical path

```
Group A (1→2→3→4)
  ↓
Group B (5, 6 sequential; 7, 15, 16, 17 parallel after 6)  [Task 8 removed — R4]
  ↓
Group C (9→10→11; 12 parallel with 10 after 9; 13 after 12)
  ↓
Group D (14) — depends on all of Group C
  ↓
Group E (18) — parallel with D
  ↓
Group F (19→20, 21 parallel with 20)
  ↓
Group G (22, 23, 24 parallel)
  ↓
Group H (25→26→27→28→29→30) — strictly sequential
  ↓
Group I (31, 32, 33, 34 parallel)
  ↓
Group J (35→36→37)
  ↓
Group K (38→39→40)
  ↓
Group L (41, 42, 43, 44 parallel)
  ↓
Group M (45, 46 may start earlier, as soon as their dependencies allow;
         47→48 sequential after Group H; 49 after 47; 50, 51 after Task 39)
```

### Parallel opportunities

| Phase | Parallel tasks | Worktree strategy |
|-------|----------------|-------------------|
| After Task 6 | 7, 15, 16, 17 | 4 parallel worktrees (core contracts) |
| After Task 9 | 10 + 12 (parallel) | 2 parallel worktrees |
| After Task 14 | 18 (Group E) parallel with Group F start (but Group F depends on 18, so sequential in practice) | - |
| After Task 19 | 20, 21 | 2 parallel worktrees |
| Group G | 22, 23, 24 | 3 parallel worktrees |
| Group I | 31, 32, 33, 34 | 4 parallel worktrees |
| Group L | 41, 42, 43, 44 | 4 parallel worktrees |
| Group M | 45 + 46 (any time after deps); 48 + 49 after 47; 50 + 51 after 39 | up to 2-3 parallel worktrees per phase |

### Strictly sequential (no parallelism possible)

- Group A (scaffolding — must be serialized)
- Group H (tick engine — each task builds on the previous)
- Group K (38→39→40 — fluent builder must be complete before AddScheduler, integration test last)

---

## Deferred Items

1. **`Bifrost.Scheduling.Marten` adapter package and the multi-instance coordination contract** — both deferred to a follow-up feature workflow. Per design DR-6/R4, no coordination interface, lease type, or leader-election API ships in v1; the contract is designed **with** the Marten adapter, not before it, so the first real store can inform its shape. The design records binding requirements for that future contract: a strictly monotonic epoch/fencing token, `LastRenewedAt`/`ExpiresAt` exposure (no bare held-flag), checkpoint writes conditional on ownership, and an abstract base class over an interface for the lease type. Rationale: the forcing use case (`basileus#145`) is single-instance, so multi-instance durability is not critical path — and an unimplemented coordination contract is a guess (research §4).

2. **`Bifrost.Scheduling.Wolverine` interop** — deferred per issue #16 "follow-up" positioning. The `IJobDispatcher` extension point is the integration hook — adapters can be added without core changes.

3. **Multi-instance auto-detection** — warning on non-exclusive-store multi-instance requires the user to set `SchedulerOptions.MultiInstanceExpected = true`. True auto-detection (e.g., Kubernetes pod count) is out of scope and probably impossible in a library context.

4. **Durable persistence implementation** — `FileScheduleStore`, `SqliteScheduleStore`, and similar — deferred. All would be separate adapter packages. The contract is the forward-compatibility boundary.

5. **Open question: package name (Scheduling vs Scheduler)** — this plan commits to `Bifrost.Scheduling`. If a naming bikeshed emerges during review, a rename is trivial (no public type rename required).

6. **Open question: `FakeTimeProvider` transitive dep** — this plan commits to shipping `FakeTimeProvider` support via the separate `Bifrost.Scheduling.Testing` package so the core `Bifrost.Scheduling` has no transitive test-provider dependency.

7. **Open question: missed-fire policy on paused-then-resumed jobs** — this plan commits to "Pause is intentional absence, so Resume does NOT trigger missed-fire policy" (missed-fire only fires on startup-from-store-recovery). Codified in Task 26 tests.

8. **Relationship to priority queues (0.5.0)** — out of scope. When priority queues ship, `DispatchTo<IPriorityWorkOrchestrator<T>>(..., priority: N)` can be added as an extension method without changes to the scheduler core.

---

## Completion Checklist

- [ ] All tests written before implementation (Iron Law)
- [ ] All 50 active tasks complete (51 numbered; Task 8 removed per design R4)
- [ ] `check_plan_coverage` passed
- [ ] `check_provenance_chain` passed — all 13 DR-N requirements trace to tasks
- [ ] `check_task_decomposition` run (advisory)
- [ ] `spec_coverage_check` passed
- [ ] `check_coverage_thresholds` passed — 80% line, 70% branch, 100% function for `Bifrost.Scheduling.Core` and `Bifrost.Scheduling`
- [ ] Benchmarks validate DR-11 merge gates (saturation correctness via Task 48, p99 < 5ms at 10K jobs, O(log n) scaling); allocation figures recorded as tracked benchmark targets, not gates
- [ ] Banned-API check (Task 46) build-failing and green; no `DateTime*.Now/UtcNow` or non-`TimeProvider` `Task.Delay` in shipping scheduling code
- [ ] CI `PublishAot` smoke + trim-warnings-as-errors green on all scheduling packages (Task 50, DR-13)
- [ ] Integration test (Task 40) confirms scheduler + orchestrator + resilience + DLQ end-to-end
- [ ] All new projects added to solution and build green
- [ ] CHANGELOG entry for the new scheduling feature
- [ ] Ready for review
