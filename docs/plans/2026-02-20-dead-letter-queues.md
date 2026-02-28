# Implementation Plan: Bifrost 0.3.0 — Dead Letter Queues

**Design:** `docs/designs/2026-02-20-dead-letter-queues.md`
**Iron Law:** No production code without a failing test first.

---

## Task Overview

| Group | Tasks | Parallelizable | Dependencies |
|-------|-------|----------------|-------------|
| A: Core Abstractions | 1–4 | Yes (within group) | None |
| B: Core Implementations | 5–9 | Partially | Group A |
| C: Integrations | 10–11 | Yes (within group) | Group B |
| D: DLQ Benchmarks | 12 | No | Group B |
| E: Benchmark Backfill | 13–16 | Yes (within group) | None |
| F: Housekeeping | 17–18 | Yes | None |

---

## Group A: Core Abstractions (Bifrost.Core)

### Task 1: DeadLetteredWork\<TWork\> Record Struct
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write test: `DeadLetteredWorkTests.cs`
   - File: `src/Bifrost.Tests/DeadLetter/DeadLetteredWorkTests.cs`
   - Tests:
     - `DeadLetteredWork_IsValueType` — verify `typeof(DeadLetteredWork<>).IsValueType` is true
     - `DeadLetteredWork_HasCorrectProperties` — verify Work, Exception, AttemptCount, FailedAt, CorrelationId properties exist with correct types
     - `Constructor_SetsPropertiesCorrectly` — instantiate and verify all values
     - `Type_IsPublic` — verify public accessibility
     - `DeadLetteredWork_ImplementsIEquatable` — verify record struct equality
   - Expected failure: `DeadLetteredWork<TWork>` type does not exist

2. **[GREEN]** Implement `DeadLetteredWork<TWork>`
   - File: `src/Bifrost.Core/DeadLetter/DeadLetteredWork.cs`
   - `public readonly record struct` with 5 positional parameters

3. **[REFACTOR]** XML docs, copyright header

**Dependencies:** None
**Parallelizable:** Yes

---

### Task 2: IDeadLetterQueue\<TWork\> Interface
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write test: `IDeadLetterQueueTests.cs`
   - File: `src/Bifrost.Tests/DeadLetter/IDeadLetterQueueTests.cs`
   - Tests:
     - `IDeadLetterQueue_IsInterface` — verify type is interface
     - `IDeadLetterQueue_HasEnqueueAsyncMethod` — verify method signature via reflection
     - `IDeadLetterQueue_HasReadAllAsyncMethod` — verify returns `IAsyncEnumerable<DeadLetteredWork<TWork>>`
     - `IDeadLetterQueue_HasCountProperty` — verify `int Count { get; }`
     - `Type_IsPublic` — verify public accessibility
   - Expected failure: `IDeadLetterQueue<TWork>` type does not exist

2. **[GREEN]** Implement `IDeadLetterQueue<TWork>`
   - File: `src/Bifrost.Core/DeadLetter/IDeadLetterQueue.cs`
   - Interface with `EnqueueAsync`, `ReadAllAsync`, `Count`

3. **[REFACTOR]** XML docs

**Dependencies:** Task 1
**Parallelizable:** Yes (with Task 1 if stubs exist)

---

### Task 3: DeadLetterQueueOptions
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write test: `DeadLetterQueueOptionsTests.cs`
   - File: `src/Bifrost.Tests/DeadLetter/DeadLetterQueueOptionsTests.cs`
   - Tests:
     - `DeadLetterQueueOptions_DefaultValues_AreCorrect` — Capacity=1000, MaxRetries=3
     - `Capacity_HasRangeAttribute` — verify `[Range(1, int.MaxValue)]`
     - `MaxRetries_HasRangeAttribute` — verify `[Range(0, 100)]`
     - `Properties_CanBeSet` — set and verify
     - `Class_IsPublic` — verify public accessibility
   - Expected failure: `DeadLetterQueueOptions` type does not exist

2. **[GREEN]** Implement `DeadLetterQueueOptions`
   - File: `src/Bifrost.Core/DeadLetter/DeadLetterQueueOptions.cs`
   - Sealed class with `[Range]` annotations, matching `WorkOrchestratorOptions` pattern

3. **[REFACTOR]** XML docs

**Dependencies:** None
**Parallelizable:** Yes

---

### Task 4: WorkDeadLetteredEvent\<TWork\>
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write test: `WorkDeadLetteredEventTests.cs`
   - File: `src/Bifrost.Tests/Events/WorkDeadLetteredEventTests.cs`
   - Tests:
     - `WorkDeadLetteredEvent_IsSealedRecordClass` — verify reference type with IEquatable
     - `WorkDeadLetteredEvent_HasCorrectProperties` — Work, Exception, AttemptCount, Timestamp, CorrelationId
     - `WorkDeadLetteredEvent_ImplementsIOrchestratorEvent` — verify `IOrchestratorEvent.IsAssignableFrom`
     - `WorkDeadLetteredEvent_ImplementsICorrelatedEvent` — verify `ICorrelatedEvent.IsAssignableFrom`
     - `Constructor_SetsPropertiesCorrectly` — instantiate and verify all values
     - `Type_IsPublic` — verify public accessibility
   - Expected failure: `WorkDeadLetteredEvent<TWork>` type does not exist

2. **[GREEN]** Implement `WorkDeadLetteredEvent<TWork>`
   - File: `src/Bifrost.Core/Events/WorkDeadLetteredEvent.cs`
   - `sealed record` implementing `ICorrelatedEvent`, matching `WorkEnqueuedEvent` pattern

3. **[REFACTOR]** XML docs

**Dependencies:** None
**Parallelizable:** Yes

---

## Group B: Core Implementations (Bifrost)

### Task 5: DeadLetterNotifier\<TWork\>
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write test: `DeadLetterNotifierTests.cs`
   - File: `src/Bifrost.Tests/DeadLetter/DeadLetterNotifierTests.cs`
   - Tests:
     - `Notify_WithSubscriber_InvokesCallback` — subscribe then notify, verify callback received event
     - `Notify_WithoutSubscriber_DoesNotThrow` — notify with no subscriber, verify no exception
     - `Subscribe_ReturnsDisposable` — verify returns IDisposable
     - `Subscribe_Dispose_RemovesSubscriber` — subscribe, dispose, notify, verify callback not called
     - `Subscribe_OverwritesPreviousSubscriber` — subscribe twice, notify, verify only latest receives
   - Expected failure: `DeadLetterNotifier<TWork>` type does not exist

2. **[GREEN]** Implement `IDeadLetterNotifier<TWork>` (internal interface) and `DeadLetterNotifier<TWork>`
   - File: `src/Bifrost/DeadLetter/IDeadLetterNotifier.cs` (internal interface)
   - File: `src/Bifrost/DeadLetter/DeadLetterNotifier.cs` (internal sealed class)
   - Single-subscriber notification bridge with `Notify`, `Subscribe`, nested `Subscription` class

3. **[REFACTOR]** XML docs

**Dependencies:** Task 4 (WorkDeadLetteredEvent)
**Parallelizable:** Yes (with Task 6)

---

### Task 6: DeadLetterQueue\<TWork\> Channel-Backed Implementation
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write test: `DeadLetterQueueTests.cs`
   - File: `src/Bifrost.Tests/DeadLetter/DeadLetterQueueTests.cs`
   - Tests:
     - `Constructor_ThrowsWhenOptionsNull` — `ArgumentNullException`
     - `EnqueueAsync_IncreasesCount` — enqueue 1 item, verify Count == 1
     - `EnqueueAsync_MultipleItems_TracksCount` — enqueue 3 items, verify Count == 3
     - `ReadAllAsync_DrainsQueue` — enqueue 3, read all, verify 3 items returned and Count == 0
     - `ReadAllAsync_EmptyQueue_ReturnsEmpty` — read from empty, verify no items
     - `ReadAllAsync_PreservesOrder` — enqueue A, B, C; read all; verify order A, B, C
     - `ReadAllAsync_PreservesFailureContext` — enqueue with exception/attempt/timestamp; read; verify preserved
     - `Count_InitiallyZero` — new DLQ has Count == 0
     - `EnqueueAsync_AtCapacity_DropsOldest` — enqueue Capacity+1 items, verify Count == Capacity and oldest dropped
     - `ImplementsIDeadLetterQueue` — verify `IsAssignableTo<IDeadLetterQueue<string>>`
   - Expected failure: `DeadLetterQueue<TWork>` type does not exist

2. **[GREEN]** Implement `DeadLetterQueue<TWork>`
   - File: `src/Bifrost/DeadLetter/DeadLetterQueue.cs`
   - Channel-backed with `BoundedChannelFullMode.DropOldest`, `Interlocked` count, non-blocking drain

3. **[REFACTOR]** Thread safety review, XML docs

**Dependencies:** Task 1, Task 2, Task 3 (DeadLetteredWork, IDeadLetterQueue, Options)
**Parallelizable:** Yes (with Task 5)

---

### Task 7: Builder Modification — HandlerDecorators Support
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write test: `WorkOrchestratorBuilderHandlerDecoratorTests.cs`
   - File: `src/Bifrost.Tests/DependencyInjection/WorkOrchestratorBuilderHandlerDecoratorTests.cs`
   - Tests:
     - `Build_WithHandlerDecorator_AppliesDecoration` — register a handler decorator that wraps handler, verify the decorated handler is used by orchestrator
     - `Build_WithMultipleHandlerDecorators_AppliesInOrder` — register two decorators, verify they compose correctly (outer wraps inner)
     - `Build_WithNoHandlerDecorators_UsesOriginalHandler` — verify existing behavior unchanged
   - Expected failure: `HandlerDecorators` property does not exist on builder

2. **[GREEN]** Modify `WorkOrchestratorBuilder<TWork>`
   - File: `src/Bifrost/DependencyInjection/WorkOrchestratorBuilder.cs`
   - Add: `internal List<Func<IServiceProvider, IWorkHandler<TWork>, IWorkHandler<TWork>>> HandlerDecorators { get; } = [];`
   - Modify `Build()`: resolve handler, apply decorators, pass to `WorkOrchestrator` constructor

3. **[REFACTOR]** Verify no existing tests break

**Dependencies:** None (modifies existing infrastructure)
**Parallelizable:** No (modifies shared builder, needed by Tasks 8–9)

---

### Task 8: DeadLetterHandler\<TWork\>
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write test: `DeadLetterHandlerTests.cs`
   - File: `src/Bifrost.Tests/DeadLetter/DeadLetterHandlerTests.cs`
   - Tests:
     - `HandleAsync_Success_DelegatesAndReturns` — inner handler succeeds, no DLQ interaction
     - `HandleAsync_Failure_RetriesUpToMaxRetries` — inner throws, verify called MaxRetries+1 times total
     - `HandleAsync_Failure_DeadLettersAfterExhaustion` — inner always throws, verify `_dlq.EnqueueAsync` called once
     - `HandleAsync_Failure_DeadLetteredWorkHasCorrectContext` — verify exception, attempt count, timestamp in dead letter
     - `HandleAsync_Failure_NotifiesOnDeadLetter` — verify `_notifier.Notify` called with correct event
     - `HandleAsync_Failure_DoesNotRethrow` — inner throws, verify HandleAsync does NOT throw (worker continues)
     - `HandleAsync_TransientFailure_SucceedsOnRetry` — inner throws once then succeeds, verify no DLQ
     - `HandleAsync_Cancellation_Rethrows` — inner throws OperationCanceledException with cancelled token, verify rethrown
     - `HandleAsync_Cancellation_NeverDeadLetters` — verify DLQ not called on cancellation
     - `HandleAsync_MaxRetries0_DeadLettersImmediately` — MaxRetries=0, inner throws once, verify dead-lettered after 1 attempt
     - `Constructor_ThrowsWhenInnerNull` — `ArgumentNullException`
     - `Constructor_ThrowsWhenDlqNull` — `ArgumentNullException`
     - `Constructor_ThrowsWhenNotifierNull` — `ArgumentNullException`
     - `Constructor_ThrowsWhenOptionsNull` — `ArgumentNullException`
     - `Constructor_ThrowsWhenLoggerNull` — `ArgumentNullException`
     - `ImplementsIWorkHandler` — verify `IsAssignableTo<IWorkHandler<string>>`
   - Expected failure: `DeadLetterHandler<TWork>` type does not exist

2. **[GREEN]** Implement `DeadLetterHandler<TWork>`
   - File: `src/Bifrost/DeadLetter/DeadLetterHandler.cs`
   - Internal sealed class, IWorkHandler wrapper with retry loop, DLQ routing, notification

3. **[REFACTOR]** Verify exception preservation, logging review

**Dependencies:** Task 1–6 (all abstractions + DLQ + notifier)
**Parallelizable:** No (central piece, needed by Task 9)

---

### Task 9: DeadLetterQueueExtensions — Builder Integration
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write test: `DeadLetterQueueExtensionsTests.cs`
   - File: `src/Bifrost.Tests/DependencyInjection/DeadLetterQueueExtensionsTests.cs`
   - Tests:
     - `WithDeadLetterQueue_RegistersDlqService` — verify `IDeadLetterQueue<string>` resolvable
     - `WithDeadLetterQueue_RegistersNotifierService` — verify internal notifier resolvable
     - `WithDeadLetterQueue_RegistersOptions` — verify options resolvable with defaults
     - `WithDeadLetterQueue_AppliesCustomOptions` — verify configured Capacity/MaxRetries applied
     - `WithDeadLetterQueue_WrapsHandler` — build orchestrator, enqueue failing work, verify item in DLQ
     - `WithDeadLetterQueue_ThrowsWhenBuilderNull` — `ArgumentNullException`
     - `WithDeadLetterQueue_ReturnsSameBuilder` — verify fluent chaining
   - Expected failure: `WithDeadLetterQueue` method does not exist

2. **[GREEN]** Implement `DeadLetterQueueExtensions`
   - File: `src/Bifrost/DependencyInjection/DeadLetterQueueExtensions.cs`
   - Static class with `WithDeadLetterQueue<TWork>` extension on builder
   - Registers options, DLQ, notifier, adds handler decorator

3. **[REFACTOR]** Ensure `TryAddSingleton` for idempotency

**Dependencies:** Task 5–8 (all implementations + builder modification)
**Parallelizable:** No (final integration of DLQ feature)

---

## Group C: Integrations

### Task 10: DeadLetterQueueHealthCheck\<TWork\>
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write test: `DeadLetterQueueHealthCheckTests.cs`
   - File: `src/Bifrost.Tests/HealthChecks/DeadLetterQueueHealthCheckTests.cs`
   - Tests:
     - `Constructor_ThrowsWhenDlqNull` — `ArgumentNullException`
     - `CheckHealthAsync_EmptyDlq_ReturnsHealthy` — Count=0, verify `HealthStatus.Healthy`
     - `CheckHealthAsync_BelowDegraded_ReturnsHealthy` — Count below degraded threshold
     - `CheckHealthAsync_AtDegradedThreshold_ReturnsDegraded` — Count at degraded threshold
     - `CheckHealthAsync_AtUnhealthyThreshold_ReturnsUnhealthy` — Count at unhealthy threshold
     - `CheckHealthAsync_IncludesDescriptiveMessage` — verify description contains count
     - `HealthCheckRegistration_RegisteredViaBuilder` — verify health check registered when both `.WithDeadLetterQueue()` and `.WithHealthChecks()` called
   - Expected failure: `DeadLetterQueueHealthCheck<TWork>` type does not exist

2. **[GREEN]** Implement `DeadLetterQueueHealthCheck<TWork>`
   - File: `src/Bifrost.HealthChecks/DeadLetterQueueHealthCheck.cs`
   - Sealed class implementing `IHealthCheck` with degraded/unhealthy thresholds
   - Modify `HealthCheckExtensions` to support DLQ health check registration

3. **[REFACTOR]** Configurable thresholds, XML docs

**Dependencies:** Task 6 (IDeadLetterQueue), Task 9 (builder extension)
**Parallelizable:** Yes (with Task 11)

---

### Task 11: OrchestratorMetrics DLQ Additions
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write test additions in `OrchestratorMetricsTests.cs`
   - File: `src/Bifrost.Tests/OpenTelemetry/OrchestratorMetricsTests.cs` (modify existing)
   - New tests:
     - `Constructor_CreatesDeadLetteredCounter` — verify `ItemsDeadLettered` property not null
     - `RecordDeadLettered_IncrementsCounter` — call `RecordDeadLettered()`, verify no exception
     - `Constructor_CreatesDlqDepthGauge` — verify `DeadLetterQueueDepth` property not null
   - Expected failure: `ItemsDeadLettered` property does not exist

2. **[GREEN]** Modify `OrchestratorMetrics<TWork>`
   - File: `src/Bifrost.OpenTelemetry/OrchestratorMetrics.cs`
   - Add `Counter<long> ItemsDeadLettered` and `ObservableGauge<int> DeadLetterQueueDepth`
   - Add `RecordDeadLettered()` method
   - Modify constructor to accept optional `Func<IDeadLetterQueue<TWork>?>` for DLQ depth gauge
   - Modify `OpenTelemetryExtensions` to wire DLQ provider if available

3. **[REFACTOR]** Backward compatibility check — ensure existing constructor still works

**Dependencies:** Task 2 (IDeadLetterQueue interface)
**Parallelizable:** Yes (with Task 10)

---

## Group D: DLQ Benchmarks

### Task 12: DeadLetterQueue Benchmarks
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write benchmark class
   - File: `src/Bifrost.Benchmarks/DeadLetter/DeadLetterQueueBenchmarks.cs`
   - Benchmarks:
     - `DlqEnqueue` — enqueue to DLQ with failure context
     - `DlqDrain` — drain DLQ items via ReadAllAsync
     - `HappyPath_NoFailure` — baseline: DeadLetterHandler with succeeding inner handler (measures try/catch overhead)
     - `FailurePath_DeadLetter` — DeadLetterHandler with throwing inner handler (measures full DLQ path)
   - Use `[MemoryDiagnoser]`, `[GlobalSetup]`, `[GlobalCleanup]`

2. **[GREEN]** Register in `Program.cs` BenchmarkSwitcher
   - File: `src/Bifrost.Benchmarks/Program.cs`
   - Add `typeof(DeadLetterQueueBenchmarks)` to switcher array

3. **[REFACTOR]** Verify dry run passes

**Dependencies:** Group B complete (all DLQ implementations)
**Parallelizable:** No

---

## Group E: Benchmark Backfill (0.2.0 Gaps)

### Task 13: PropertyAccessBenchmarks
**Phase:** GREEN (benchmarks are not TDD — they validate, not specify)

1. **[GREEN]** Implement benchmark
   - File: `src/Bifrost.Benchmarks/Core/PropertyAccessBenchmarks.cs`
   - Benchmarks: `PendingCount`, `ActiveWorkers`, `Capacity` property access
   - Pattern: `[MemoryDiagnoser]`, `[GlobalSetup]` creates orchestrator, measures property reads
   - Register in `Program.cs`

**Dependencies:** None
**Parallelizable:** Yes (with Tasks 14–16)

---

### Task 14: EventStreamOverheadBenchmarks
**Phase:** GREEN

1. **[GREEN]** Implement benchmark
   - File: `src/Bifrost.Benchmarks/Decorators/EventStreamOverheadBenchmarks.cs`
   - Benchmarks: `TryEnqueue_WithEventStream` vs bare `TryEnqueue` (baseline from existing DecoratorOverheadBenchmarks)
   - Measures channel broadcast cost
   - Register in `Program.cs`

**Dependencies:** None
**Parallelizable:** Yes (with Tasks 13, 15–16)

---

### Task 15: WorkerRegistryBenchmarks
**Phase:** GREEN

1. **[GREEN]** Implement benchmark
   - File: `src/Bifrost.Benchmarks/Autoscaling/WorkerRegistryBenchmarks.cs`
   - Benchmarks: `GetWorkerInfo`, `CreateWorkerAsync`, `GetAllWorkers` operations
   - Register in `Program.cs`

**Dependencies:** None
**Parallelizable:** Yes (with Tasks 13–14, 16)

---

### Task 16: HealthCheckBenchmarks
**Phase:** GREEN

1. **[GREEN]** Implement benchmark
   - File: `src/Bifrost.Benchmarks/HealthChecks/HealthCheckBenchmarks.cs`
   - Benchmarks: `CheckHealthAsync` latency for orchestrator and autoscaling health checks
   - Register in `Program.cs`

**Dependencies:** None
**Parallelizable:** Yes (with Tasks 13–15)

---

## Group F: Housekeeping

### Task 17: Close Issue #2
**Phase:** HOUSEKEEPING

1. Close GitHub Issue #2 ("Update Lvlup.Build to v1.3.0 and add format-check CI") — format-check CI was delivered in PR #3

**Dependencies:** None
**Parallelizable:** Yes

---

### Task 18: Cut v0.2.0 Tag
**Phase:** HOUSEKEEPING

1. Create annotated tag `v0.2.0` on current `main` HEAD
2. Verify tag points to correct commit (post-PR #4)

**Dependencies:** None
**Parallelizable:** Yes

---

## Dependency Graph

```text
Group A (parallel):    [1] [2] [3] [4]
                        │   │   │   │
                        └───┴───┴───┘
                              │
Group B (sequential):        [5,6] ──→ [7] ──→ [8] ──→ [9]
                              │
Group C (parallel):          [10] [11]
                              │
Group D:                     [12]

Group E (parallel):    [13] [14] [15] [16]

Group F (parallel):    [17] [18]
```

**Delegation strategy for agent teams:**

| Worktree | Tasks | Rationale |
|----------|-------|-----------|
| Agent 1 (DLQ Core) | 1–9, 12 | Sequential DLQ feature chain |
| Agent 2 (Integrations) | 10–11 | Health checks + OTel (after Agent 1 completes Group A+B) |
| Agent 3 (Benchmark Backfill) | 13–16 | Independent, parallelizable |
| Agent 4 (Housekeeping) | 17–18 | Independent GitHub operations |

---

## Verification Checklist

After all tasks complete:

- [ ] `dotnet build src/Bifrost.sln` — no errors
- [ ] `dotnet test src/Bifrost.Tests/` — all tests pass
- [ ] `dotnet run --project src/Bifrost.Benchmarks/ -c Release -- --job Dry --filter "*DeadLetter*"` — benchmark smoke test
- [ ] `dotnet run --project src/Bifrost.Benchmarks/ -c Release -- --job Dry --filter "*PropertyAccess*"` — backfill smoke test
- [ ] All new types have XML documentation
- [ ] All new files have copyright header
- [ ] No existing tests broken
- [ ] Ensure test coverage: >90% line coverage, >70% branch coverage
