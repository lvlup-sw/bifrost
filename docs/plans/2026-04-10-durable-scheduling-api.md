# Implementation Plan: Bifrost Durable Scheduling API

**Design:** `docs/designs/2026-04-10-durable-scheduling-api.md`
**Issue:** [#16](https://github.com/lvlup-sw/bifrost/issues/16)
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
- `IExclusiveScheduleStore` + `ILeadershipLease` **contracts only**; in-memory always-leader behavior — DR-6
- `ScheduleTickLoop` with priority-queue engine and wake-channel for mutations — DR-7
- Observability: metrics, events, health check, inspector API — DR-8
- `ISchedulerTestHarness` with `FakeTimeProvider` support — DR-9
- Full error-handling surface — DR-10
- Benchmark suite in `Bifrost.Benchmarks/Scheduling/` — DR-11

**Excluded (follow-up releases, explicitly deferred):**
- `Bifrost.Scheduling.Marten` adapter package — deferred to follow-up release. The `IExclusiveScheduleStore` contract is in scope so the adapter can be built without core changes.
- `Bifrost.Scheduling.Wolverine` interop adapter — deferred per issue #16 "Wolverine interop surface" section.
- Any persistent storage implementation beyond in-memory — deferred; the contract is the forward-compatibility boundary.
- Durable multi-instance leader election — the contract ships, no concrete implementation ships.

## Summary

- **Total tasks:** 44
- **Parallel groups:** 11 (A through K)
- **Estimated test count:** ~140
- **Design coverage:** 11 of 11 DR-N requirements covered

---

## Spec Traceability

### Design Requirement → Task Mapping

| DR | Requirement | Tasks |
|----|-------------|-------|
| DR-1 | Job registry with fluent and runtime APIs | 5, 6, 15, 19, 20, 21, 38, 39 |
| DR-2 | Cadence primitives | 9, 10, 11, 12, 13, 38, 43 |
| DR-3 | Missed-fire policies | 5, 14, 26, 38 |
| DR-4 | Pluggable dispatch — orchestrator, inline, and custom | 16, 22, 23, 24, 30, 38, 40, 44 |
| DR-5 | Pluggable storage via IScheduleStore | 6, 7, 18, 39 |
| DR-6 | Coordination — single-leader default, opt-in exclusivity | 5, 8, 27, 39 |
| DR-7 | Timer-wheel tick engine | 25, 26, 42 |
| DR-8 | Observability — metrics, events, and health checks | 17, 31, 32, 33, 34 |
| DR-9 | Testing primitives — FakeTimeProvider + deterministic fire control | 3, 35, 36, 37 |
| DR-10 | Error handling and edge cases | 13, 26, 28, 29, 30 |
| DR-11 | Performance and allocation targets | 41, 42, 43, 44 |

### Design Section → Task Mapping

This table maps every Technical Design subsection of `docs/designs/2026-04-10-durable-scheduling-api.md` to implementing tasks.

| Design Section | Tasks |
|----------------|-------|
| Package layout | 1, 2, 3, 4 (scaffolds all three packages + test project per the Package layout diagram) |
| Type model | 5, 6, 7, 8, 9, 10, 11, 12, 15, 16, 17 (all types in the Type model code block) |
| Tick loop (pseudo-code) | 25, 26, 27, 28, 29, 30 (implements the `ScheduleTickLoop` pseudo-code loop) |
| Fluent builder integration | 38, 39 (`ISchedulerBuilder`, `AddJob<TWork>`, `AddInlineJob`, `UseStore<T>`) |
| DR-1: Job registry with fluent and runtime APIs | 5, 6, 15, 19, 20, 21, 38, 39 |
| DR-2: Cadence primitives | 9, 10, 11, 12, 13, 38, 43 |
| DR-3: Missed-fire policies | 5, 14, 26, 38 |
| DR-4: Pluggable dispatch — orchestrator, inline, and custom | 16, 22, 23, 24, 30, 38, 40, 44 |
| DR-5: Pluggable storage via IScheduleStore | 6, 7, 18, 39 |
| DR-6: Coordination — single-leader default, opt-in exclusivity | 5, 8, 27, 39 |
| DR-7: Timer-wheel tick engine | 25, 26, 42 |
| DR-8: Observability — metrics, events, and health checks | 17, 31, 32, 33, 34 |
| DR-9: Testing primitives — FakeTimeProvider + deterministic fire control | 3, 35, 36, 37 |
| DR-10: Error handling and edge cases | 13, 26, 28, 29, 30 |
| DR-11: Performance and allocation targets | 41, 42, 43, 44 |

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

### Task 8: IExclusiveScheduleStore + ILeadershipLease
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** unit
**Implements:** DR-6

1. **[RED]** `src/Bifrost.Tests.Scheduling/Core/IExclusiveScheduleStoreTests.cs`
   - `IExclusiveScheduleStore_ExtendsIScheduleStore`
   - `IExclusiveScheduleStore_HasTryAcquireLeadershipAsyncMethod` — signature `ValueTask<ILeadershipLease?> TryAcquireLeadershipAsync(CancellationToken)`
   - `ILeadershipLease_ImplementsIAsyncDisposable`
   - `ILeadershipLease_HasIsHeldProperty`
   - `ILeadershipLease_HasRenewAsyncMethod`
   - Expected failure: types don't exist

2. **[GREEN]**
   - `src/Bifrost.Scheduling.Core/IExclusiveScheduleStore.cs`
   - `src/Bifrost.Scheduling.Core/ILeadershipLease.cs`

3. **[REFACTOR]** XML docs noting "no concrete multi-instance implementation ships in v1; adapter packages required"

**Dependencies:** Task 7
**Parallelizable:** Yes

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
   - `Cadence_After_ReturnsOneShotCadenceWithNowPlusDelay` — uses TimeProvider
   - Expected failure: types don't exist / are stubs from Task 6

2. **[GREEN]**
   - Replace `Cadence` stub with `public abstract record Cadence` exposing abstract `ComputeNextFire(DateTimeOffset? lastFiredAt, DateTimeOffset now)`
   - Add static factory methods `Interval`, `Cron`, `At`, `After` (stubs for non-OneShot types; filled in later tasks)
   - `src/Bifrost.Scheduling.Core/OneShotCadence.cs` — `public sealed record OneShotCadence(DateTimeOffset FireAt) : Cadence`

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
   - All events are zero-allocation when fields fit in struct layout

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

### Task 27: ScheduleTickLoop — single-leader behavior and exclusive-store warning
**Phase:** RED → GREEN → REFACTOR
**Test Layer:** integration
**Acceptance Test Ref:** Task 25
**Implements:** DR-6

1. **[RED]** `src/Bifrost.Tests.Scheduling/TickEngine/LeadershipTests.cs`
   - `TickLoop_InMemoryStore_AssumesLeadership_Fires` — in-memory store, loop ticks normally
   - `TickLoop_ExclusiveStore_AcquiresLeadership` — mock `IExclusiveScheduleStore`, verify `TryAcquireLeadershipAsync` called
   - `TickLoop_ExclusiveStore_LeadershipDenied_DoesNotFire` — returns null lease, loop waits
   - `TickLoop_ExclusiveStore_LeaseReleased_StopsFiring` — `IsHeld = false`, loop pauses firing
   - `TickLoop_NonExclusiveStore_Multinstance_LogsWarning` — startup log contains warning about duplicate fires

2. **[GREEN]**
   - Add leadership check to tick loop startup
   - If `IScheduleStore is IExclusiveScheduleStore exclusive`, call `TryAcquireLeadershipAsync`
   - Add periodic `RenewAsync` (configurable interval via options)
   - If lease denied, enter idle wait and retry after backoff
   - Emit warning log if non-exclusive store + multi-instance detection heuristic (not auto-detected in v1; log only on explicit `SchedulerOptions.MultiInstanceExpected = true`)

3. **[REFACTOR]** Extract `LeadershipManager` helper

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
   - `Shutdown_ReleasesLeadershipLease` — mock exclusive store, verify `DisposeAsync` called on lease

2. **[GREEN]**
   - Override `StopAsync` in `ScheduleTickLoop`
   - Track in-flight dispatches via `CountdownEvent` or similar
   - Wait up to `SchedulerOptions.ShutdownTimeout`
   - Dispose leadership lease in finally

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
   - `After_TimeSpan_SetsOneShotCadence`
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

## Group L: Benchmarks — Performance and allocation targets

Implements **DR-11: Performance and allocation targets**. Validates the zero-allocation steady-state goal, p99 fire latency target, and 10K-job scaling claim.

### Task 41: Benchmarks — Performance and allocation targets (registry operations)
**Phase:** Benchmark
**Test Layer:** benchmark
**Implements:** DR-11

**Steps:**
1. Create `src/Bifrost.Benchmarks/Scheduling/` directory
2. `RegistryRegistrationBenchmarks.cs` — `[MemoryDiagnoser]`
   - `Register_Single` — register one job, measure allocation and time
   - `Register_Bulk` — register 1000 jobs
   - `Pause_Single`, `Resume_Single`, `Trigger_Single`
   - `GetJobs_1000Jobs` — allocation target <256B single result list
3. Add `Bifrost.Benchmarks.csproj` ProjectReference to `Bifrost.Scheduling.csproj`

**Verification:**
- Benchmarks run via `dotnet run -c Release --project src/Bifrost.Benchmarks -- --job Dry -f "*Scheduling*"`

**testingStrategy:** `benchmarks: true`

**Dependencies:** Task 39
**Parallelizable:** Yes

---

### Task 42: Benchmarks — Performance and allocation targets (tick engine)
**Phase:** Benchmark
**Test Layer:** benchmark
**Implements:** DR-7, DR-11

**Steps:**
1. `TickEngineBenchmarks.cs` — `[MemoryDiagnoser]`, `[Params(10, 100, 1000, 10000)] public int JobCount`
   - `FireLatency` — measure p99 from `NextFireAt` to dispatch handoff
   - `SteadyStateAllocation` — 0-B target per fire (via allocation counter in body)
2. Note: IterationSetup for async work per Bifrost memory rules — use `void` with `.AsTask().GetAwaiter().GetResult()`

**Verification:**
- Benchmark runs and reports allocation < 1B per fire for 10K jobs

**testingStrategy:** `benchmarks: true`

**Dependencies:** Task 39
**Parallelizable:** Yes

---

### Task 43: Benchmarks — Performance and allocation targets (cadence compute)
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

### Task 44: Benchmarks — Performance and allocation targets (dispatch overhead)
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

## Parallelization Strategy

### Sequential critical path

```
Group A (1→2→3→4)
  ↓
Group B (5, 6 sequential; 7, 8, 15, 16, 17 parallel after 6)
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
```

### Parallel opportunities

| Phase | Parallel tasks | Worktree strategy |
|-------|----------------|-------------------|
| After Task 6 | 7, 8, 15, 16, 17 | 5 parallel worktrees (core contracts) |
| After Task 9 | 10 + 12 (parallel) | 2 parallel worktrees |
| After Task 14 | 18 (Group E) parallel with Group F start (but Group F depends on 18, so sequential in practice) | - |
| After Task 19 | 20, 21 | 2 parallel worktrees |
| Group G | 22, 23, 24 | 3 parallel worktrees |
| Group I | 31, 32, 33, 34 | 4 parallel worktrees |
| Group L | 41, 42, 43, 44 | 4 parallel worktrees |

### Strictly sequential (no parallelism possible)

- Group A (scaffolding — must be serialized)
- Group H (tick engine — each task builds on the previous)
- Group K (38→39→40 — fluent builder must be complete before AddScheduler, integration test last)

---

## Deferred Items

1. **`Bifrost.Scheduling.Marten` adapter package** — `IExclusiveScheduleStore` ships without concrete implementation. Marten adapter is a follow-up feature workflow. Rationale: the forcing use case (`basileus#145`) is single-instance, so multi-instance durability is not critical path. The contract is designed to accommodate Marten without changes.

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
- [ ] All 44 tasks complete
- [ ] `check_plan_coverage` passed
- [ ] `check_provenance_chain` passed — all 11 DR-N requirements trace to tasks
- [ ] `check_task_decomposition` run (advisory)
- [ ] `spec_coverage_check` passed
- [ ] `check_coverage_thresholds` passed — 80% line, 70% branch, 100% function for `Bifrost.Scheduling.Core` and `Bifrost.Scheduling`
- [ ] Benchmarks validate DR-11 targets (0-B steady state, p99 < 5ms at 10K jobs)
- [ ] Integration test (Task 40) confirms scheduler + orchestrator + resilience + DLQ end-to-end
- [ ] All new projects added to solution and build green
- [ ] CHANGELOG entry for the new scheduling feature
- [ ] Ready for review
