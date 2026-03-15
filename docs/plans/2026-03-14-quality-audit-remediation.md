# Implementation Plan: Quality Audit Remediation

**Design:** `docs/designs/2026-03-14-quality-audit-remediation.md`
**Issue:** #11
**Date:** 2026-03-14
**Tasks:** 9 (6 parallel + 2 sequential + 1 final)

---

## Parallelization Strategy

```
Wave 1 (parallel):  Task 1 | Task 2 | Task 3 | Task 4 | Task 5 | Task 6
                                       │
Wave 2 (after T3):                     └──→ Task 7
                                                │
Wave 3 (after all):                             └──→ Task 8
                                                      │
                                                      └──→ Task 9
```

**Wave 1:** Six independent tasks touching non-overlapping files
**Wave 2:** Autoscaling completion (depends on Task 3's ScalingAction consolidation)
**Wave 3:** Documentation pass (touches files from Task 7)
**Final:** Integration test (depends on all production code)

---

## Task 1: Concurrency & Shutdown Hardening (M1, M11, L3)

**Findings:** M1 (volatile bool race), M11 (DisposeAsync timeout), L3 (worker log context)
**Files:** `Bifrost/Autoscaling/WorkerInfo.cs`, `Bifrost/WorkOrchestrator.cs`
**Parallelizable:** Yes (Wave 1)
**Dependencies:** None

### 1a. [RED] WorkerInfo — Interlocked for _stopRequested

Write test: `RequestStop_UsesInterlocked_VisibleAcrossThreads`
- File: `Bifrost.Tests/Autoscaling/WorkerInfoTests.cs`
- Test: Verify `StopRequested` returns true after `RequestStop()` called from another thread
- Also test: Multiple `RequestStop()` calls are idempotent
- Expected failure: Existing implementation uses `volatile bool`, new test should verify `Interlocked` semantics

### 1b. [GREEN] Implement Interlocked for _stopRequested

- File: `Bifrost/Autoscaling/WorkerInfo.cs`
- Change `_stopRequested` from `volatile bool` to `private int _stopRequested`
- Change `StopRequested` property to `Volatile.Read(ref _stopRequested) == 1`
- Change `RequestStop()` to use `Interlocked.Exchange(ref _stopRequested, 1)`

### 1c. [RED] WorkOrchestrator — DisposeAsync timeout

Write test: `DisposeAsync_WithSlowWorker_CompletesWithinTimeout`
- File: `Bifrost.Tests/WorkOrchestratorTests.cs`
- Test: Create orchestrator with handler that blocks indefinitely, call DisposeAsync, verify it completes within ~6 seconds (5s timeout + buffer)
- Expected failure: Current DisposeAsync waits indefinitely

### 1d. [GREEN] Add timeout to DisposeAsync

- File: `Bifrost/WorkOrchestrator.cs`
- Add 5-second timeout using `WaitAsync(timeoutCts.Token)`
- Log warning on timeout
- Catch `OperationCanceledException`

### 1e. [RED] WorkOrchestrator — Work item context in error logs

Write test: `WorkerLoop_HandlerThrows_LogsWorkItemContext`
- File: `Bifrost.Tests/WorkOrchestratorTests.cs`
- Test: Enqueue a work item, handler throws, verify log message includes the work item
- Expected failure: Current log omits work item

### 1f. [GREEN] Add work item to error log

- File: `Bifrost/WorkOrchestrator.cs`
- Change `LogError` in `WorkerLoopAsync` catch block to include `{WorkItem}` parameter

### 1g. [REFACTOR] Clean up

- Ensure consistent naming in both `WorkerLoopAsync` and `CreateWorkerFunction` paths

---

## Task 2: DLQ Hardening (H3, M4, M6)

**Findings:** H3 (silent drops), M4 (unnecessary interface), M6 (silent subscriber replacement)
**Files:** `Bifrost/DeadLetter/DeadLetterQueue.cs`, `Bifrost/DeadLetter/DeadLetterNotifier.cs`, `Bifrost/DeadLetter/IDeadLetterNotifier.cs`, `Bifrost/DeadLetter/DeadLetterHandler.cs`, `Bifrost/DependencyInjection/DeadLetterQueueExtensions.cs`
**Parallelizable:** Yes (Wave 1)
**Dependencies:** None

### 2a. [RED] DeadLetterQueue — DroppedCount and logging

Write tests:
- `EnqueueAsync_AtCapacity_IncrementsDroppedCount`
- `EnqueueAsync_AtCapacity_LogsWarning`
- `DroppedCount_Initially_ReturnsZero`
- File: `Bifrost.Tests/DeadLetter/DeadLetterQueueTests.cs`
- Expected failure: No `DroppedCount` property exists, no logger injected

### 2b. [GREEN] Add DroppedCount + logger to DeadLetterQueue

- File: `Bifrost/DeadLetter/DeadLetterQueue.cs`
- Add `ILogger<DeadLetterQueue<TWork>>` constructor parameter
- Add `private long _droppedCount` field
- Add `public long DroppedCount => Volatile.Read(ref _droppedCount)` property
- Before `WriteAsync`: check `_channel.Reader.CanCount && _channel.Reader.Count >= _capacity`, if true increment counter and log warning
- Store capacity from options for comparison

### 2c. [RED] DeadLetterNotifier — Throw on duplicate subscription

Write tests:
- `Subscribe_WhenAlreadySubscribed_ThrowsInvalidOperationException`
- `Subscribe_AfterDispose_AllowsResubscription`
- File: `Bifrost.Tests/DeadLetter/DeadLetterNotifierTests.cs`
- Expected failure: Current implementation silently replaces

### 2d. [GREEN] Implement atomic subscription guard

- File: `Bifrost/DeadLetter/DeadLetterNotifier.cs`
- Use `Interlocked.CompareExchange` to atomically check-and-set `_callback`
- Throw `InvalidOperationException` if already subscribed
- Update `Subscription.Dispose` to null out `_callback` (enables re-subscribe after dispose)

### 2e. [RED → GREEN] Remove IDeadLetterNotifier interface (M4)

- Delete `Bifrost/DeadLetter/IDeadLetterNotifier.cs`
- Update `DeadLetterHandler.cs`: change constructor to accept `DeadLetterNotifier<TWork>` instead of `IDeadLetterNotifier<TWork>`
- Update `DeadLetterQueueExtensions.cs`: register `DeadLetterNotifier<TWork>` as concrete type
- Update `Bifrost.Tests/DeadLetter/DeadLetterHandlerTests.cs`: use concrete type
- Update `Bifrost.Tests/DependencyInjection/DeadLetterQueueExtensionsTests.cs`: verify concrete registration
- Compile and run existing tests to verify no regressions

### 2f. [REFACTOR] Update DI registration for logger

- File: `Bifrost/DependencyInjection/DeadLetterQueueExtensions.cs`
- Ensure `ILogger<DeadLetterQueue<TWork>>` is resolved from DI

---

## Task 3: Type Consolidation & Options (M3, L2)

**Findings:** M3 (duplicate ScalingAction), L2 (mutable AutoscalingOptions)
**Files:** `Bifrost/Autoscaling/ScalingAction.cs` (DELETE), `Bifrost.Core/Events/ScalingEvent.cs`, `Bifrost/Autoscaling/AutoscalingOptions.cs`
**Parallelizable:** Yes (Wave 1)
**Dependencies:** None (but Task 7 depends on this)

### 3a. [RED] Verify ScalingAction consolidated to Core namespace

Write test: `ScalingAction_ExistsOnlyInCoreEvents`
- File: `Bifrost.Tests/Events/ScalingEventTests.cs`
- Verify `typeof(Bifrost.Core.Events.ScalingAction)` exists
- Verify explicit values: None=0, ScaleUp=1, ScaleDown=2

### 3b. [GREEN] Consolidate ScalingAction enum

- Delete `Bifrost/Autoscaling/ScalingAction.cs`
- Add explicit values to `Bifrost.Core/Events/ScalingEvent.cs` enum
- Update all `using` statements referencing `Bifrost.Autoscaling.ScalingAction` to use `Bifrost.Core.Events`
- Files affected: `AutoscalingEngine.cs`, `ScalingDecision.cs`, any test files

### 3c. [RED] AutoscalingOptions — Verify immutability after construction

Write test: `AutoscalingOptions_Properties_AreInitOnly`
- File: `Bifrost.Tests/Autoscaling/AutoscalingOptionsTests.cs` (or Configuration/)
- Use reflection to verify all settable properties have `init` accessor (IsInitOnly = true on setter)
- Expected failure: Current properties have `set` accessors

### 3d. [GREEN] Change AutoscalingOptions to init accessors

- File: `Bifrost/Autoscaling/AutoscalingOptions.cs`
- Change all `{ get; set; }` to `{ get; init; }`
- Verify Options Pattern binding still works (init setters are supported)

### 3e. [REFACTOR] Clean up any leftover ScalingAction references

---

## Task 4: Decorator Ordering Validation (M2, M7)

**Findings:** M2 (duplicate order not validated), M7 (handler decorators lack ordering)
**Files:** `Bifrost/DependencyInjection/WorkOrchestratorBuilder.cs`
**Parallelizable:** Yes (Wave 1)
**Dependencies:** None

### 4a. [RED] Validate unique orchestrator decorator orders

Write tests:
- `Build_WithDuplicateDecoratorOrder_ThrowsInvalidOperationException`
- `Build_WithUniqueDecoratorOrders_Succeeds`
- File: `Bifrost.Tests/DependencyInjection/WorkOrchestratorBuilderTests.cs`
- Expected failure: No validation exists

### 4b. [GREEN] Add order uniqueness validation

- File: `Bifrost/DependencyInjection/WorkOrchestratorBuilder.cs`
- In `Build()`, before sorting: group by Order, check for duplicates, throw `InvalidOperationException`

### 4c. [RED] Handler decorators — Add Order property

Write tests:
- `Build_WithOrderedHandlerDecorators_AppliesInOrder`
- `Build_WithDuplicateHandlerDecoratorOrder_ThrowsInvalidOperationException`
- File: `Bifrost.Tests/DependencyInjection/WorkOrchestratorBuilderHandlerDecoratorTests.cs`
- Expected failure: No Order property on handler decorator registrations

### 4d. [GREEN] Add Order to handler decorator registrations

- File: `Bifrost/DependencyInjection/WorkOrchestratorBuilder.cs`
- Create `HandlerDecoratorRegistration<TWork>` record with `Order` property (matching `DecoratorRegistration`)
- Change `HandlerDecorators` from `List<Func<...>>` to `List<HandlerDecoratorRegistration<TWork>>`
- Sort by Order in `Build()` before applying
- Apply same uniqueness validation
- Update builder extension methods to accept optional `order` parameter

### 4e. [REFACTOR] Ensure backward compatibility

- Default Order=0 for handler decorators added without explicit order
- Verify existing tests still pass

---

## Task 5: Contract Tests (M14, M15, L9)

**Findings:** M14 (incomplete IWorkOrchestrator tests), M15 (no IEventStreamOrchestrator tests), L9 (no member count detection)
**Files:** `Bifrost.Tests/Contracts/IWorkOrchestratorTests.cs`, `Bifrost.Tests/Contracts/IEventStreamOrchestratorTests.cs`
**Parallelizable:** Yes (Wave 1)
**Dependencies:** None

### 5a. [RED] IWorkOrchestrator — Missing member signatures

Write tests:
- `Run_HasCorrectSignature` — void return, `TWork` parameter
- `TryRun_HasCorrectSignature` — bool return, `TWork` parameter
- `CreateWorkerFunction_HasCorrectSignature` — returns `Func<string, CancellationToken, Task>`
- `CreateWorkerFunction_WithCallback_HasCorrectSignature` — `Action<bool>?` parameter
- `RequestScaleUpAsync_HasCorrectSignature` — Task return, int + CancellationToken parameters
- `RequestScaleDownAsync_HasCorrectSignature` — Task return, int + CancellationToken parameters
- `GetShutdownToken_HasCorrectSignature` — returns CancellationToken
- File: `Bifrost.Tests/Contracts/IWorkOrchestratorTests.cs`
- Expected failure: Tests don't exist yet (but implementation does — these are RED because tests are new)

### 5b. [GREEN] Add member signature verification tests

- File: `Bifrost.Tests/Contracts/IWorkOrchestratorTests.cs`
- Follow existing reflection pattern: `typeof(IWorkOrchestrator<>).GetMethod(...)`

### 5c. [RED] IWorkOrchestrator — Member count detection

Write test: `Interface_HasExpectedMemberCount`
- File: `Bifrost.Tests/Contracts/IWorkOrchestratorTests.cs`
- Count all declared methods + properties (excluding inherited)
- Assert equals expected count
- Expected failure: Test doesn't exist

### 5d. [GREEN] Implement member count test

- Use `typeof(IWorkOrchestrator<>).GetMembers(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance)`

### 5e. [RED] IEventStreamOrchestrator — Extend contract tests

Write tests:
- `EnqueueAsync_WithCorrelationId_HasCorrectSignature`
- `TryEnqueue_WithCorrelationId_HasCorrectSignature`
- `Interface_HasExpectedMemberCount`
- File: `Bifrost.Tests/Contracts/IEventStreamOrchestratorTests.cs`
- Expected failure: Tests don't exist

### 5f. [GREEN] Add IEventStreamOrchestrator signature tests

- Follow same reflection pattern as IWorkOrchestratorTests

---

## Task 6: Event Stream Hardening (M10, M13, L6)

**Findings:** M10 (unbounded subscriber growth), M13 (silent cleanup failure), L6 (WorkCompletedEvent not published)
**Files:** `Bifrost/Decorators/EventStreamOrchestrator.cs`
**Parallelizable:** Yes (Wave 1)
**Dependencies:** None

### 6a. [RED] Stale subscriber cleanup

Write tests:
- `PublishToSubscribers_WithFullSubscriberBuffer_RemovesStaleSubscriber`
- `PublishToSubscribers_WithFullSubscriberBuffer_LogsWarning`
- File: `Bifrost.Tests/Decorators/EventStreamOrchestratorTests.cs`
- Expected failure: No stale subscriber cleanup exists

### 6b. [GREEN] Add subscriber buffer overflow cleanup

- File: `Bifrost/Decorators/EventStreamOrchestrator.cs`
- In `PublishToSubscribers()`, after TryWrite loop: check each subscriber's Reader.Count >= capacity
- If full: TryRemove, TryComplete writer, log warning

### 6c. [RED] Log subscriber cleanup failure

Write test: `ReadWithCleanup_TryRemoveFails_LogsWarning`
- File: `Bifrost.Tests/Decorators/EventStreamOrchestratorTests.cs`
- Expected failure: No log on TryRemove failure

### 6d. [GREEN] Add warning log on TryRemove failure

- File: `Bifrost/Decorators/EventStreamOrchestrator.cs`
- In `ReadWithCleanup` finally block: log warning if `TryRemove` returns false

### 6e. [RED] Publish WorkCompletedEvent

Write tests:
- `HandleWorkItem_OnSuccess_PublishesWorkCompletedEvent`
- `HandleWorkItem_OnFailure_PublishesWorkCompletedEventWithSuccessFalse`
- File: `Bifrost.Tests/Decorators/EventStreamOrchestratorTests.cs`
- Expected failure: WorkCompletedEvent is never published

### 6f. [GREEN] Wire WorkCompletedEvent publishing

- File: `Bifrost/Decorators/EventStreamOrchestrator.cs`
- After handler completes successfully: `PublishToSubscribers(new WorkCompletedEvent<TWork>(work, elapsed, success: true))`
- On handler failure: `PublishToSubscribers(new WorkCompletedEvent<TWork>(work, elapsed, success: false))`
- Requires wrapping handler execution with `Stopwatch` for duration

### 6g. [REFACTOR] Extract event publishing helper

---

## Task 7: Autoscaling Completion (H1, H2, H4, M5, M8, M9, M12, L1, L4, L8)

**Findings:** H1 (disconnected channel), H2 (coordinator stubs), H4 (empty try block), M5 (fallback constructor), M8 (fire-and-forget tasks), M9 (event handler doc), M12 (worker startup tracking), L1 (Enabled simplification), L4 (dynamic worker logging), L8 (bare options)
**Files:** `Bifrost/Autoscaling/AutoscalingCoordinator.cs`, `Bifrost/Decorators/AutoscalingOrchestrator.cs`, `Bifrost/Autoscaling/AutoscalingEngine.cs`, `Bifrost/Autoscaling/WorkerRegistry.cs`, `Bifrost/DependencyInjection/AutoscalingExtensions.cs`
**Parallelizable:** No — Wave 2 (depends on Task 3)
**Dependencies:** Task 3 (ScalingAction consolidation)

### 7a. [RED] AutoscalingCoordinator — Control port: CreateWorkerFunction

Write tests:
- `CreateWorkerFunction_DelegatesToInnerOrchestrator`
- `CreateWorkerFunction_WithCallback_DelegatesToInnerOrchestrator`
- File: `Bifrost.Tests/Autoscaling/AutoscalingCoordinatorTests.cs`
- Expected failure: Methods throw NotImplementedException

### 7b. [GREEN] Implement CreateWorkerFunction delegation

- File: `Bifrost/Autoscaling/AutoscalingCoordinator.cs`
- Add `IWorkerRegistry` parameter to constructor
- `CreateWorkerFunction()` → `_orchestrator.CreateWorkerFunction()`
- `CreateWorkerFunction(cb)` → `_orchestrator.CreateWorkerFunction(cb)`

### 7c. [RED] AutoscalingCoordinator — Control port: Scaling operations

Write tests:
- `RequestScaleUpAsync_CreatesWorkersViaRegistry`
- `RequestScaleDownAsync_StopsWorkersViaRegistry`
- `GetShutdownToken_DelegatesToOrchestrator`
- File: `Bifrost.Tests/Autoscaling/AutoscalingCoordinatorTests.cs`
- Expected failure: Methods throw NotImplementedException

### 7d. [GREEN] Implement scaling operations

- File: `Bifrost/Autoscaling/AutoscalingCoordinator.cs`
- `RequestScaleUpAsync(n, ct)` → loop n times calling `_registry.CreateWorkerAsync(id, CreateWorkerFunction(stateCallback), ct)`
- `RequestScaleDownAsync(n, ct)` → `_registry.RequestMultipleWorkerStop(n)` + return Task.CompletedTask
- `GetShutdownToken()` → `_orchestrator.GetShutdownToken()`

### 7e. [RED] AutoscalingCoordinator — Events port

Write tests:
- `PublishEvent_WithEventStream_DelegatesToEventStream`
- `PublishEvent_WithoutEventStream_NoOp`
- `QueuedEventCount_ReturnsZeroWhenNoEventStream`
- `ActiveSubscriberCount_ReturnsZeroWhenNoEventStream`
- File: `Bifrost.Tests/Autoscaling/AutoscalingCoordinatorTests.cs`
- Expected failure: Methods throw NotImplementedException

### 7f. [GREEN] Implement events port

- File: `Bifrost/Autoscaling/AutoscalingCoordinator.cs`
- Add optional `IEventStreamOrchestrator<TWork>?` constructor parameter
- `PublishEvent(evt)` → `_eventStream?.PublishToSubscribers(evt)` (requires exposing method or using internal access)
- Properties → delegate or return 0 when null

### 7g. [RED] AutoscalingOrchestrator — Rewrite RequestScaleUpAsync

Write tests:
- `RequestScaleUpAsync_UsesCoordinatorWorkerFunction`
- `RequestScaleUpAsync_RegistersWorkersWithRegistry`
- `RequestScaleUpAsync_WorkersReceiveRealWorkItems` (integration-level: enqueue item, dynamic worker processes it)
- File: `Bifrost.Tests/Decorators/AutoscalingOrchestratorTests.cs`
- Expected failure: Current implementation uses broken AsChannelReader

### 7h. [GREEN] Rewrite RequestScaleUpAsync + delete AsChannelReader

- File: `Bifrost/Decorators/AutoscalingOrchestrator.cs`
- Delete `AsChannelReader()` extension method
- Delete empty try block (H4)
- Rewrite `RequestScaleUpAsync` to:
  1. Get worker function from coordinator: `var workerFunc = CreateWorkerFunction(stateCallback)`
  2. Register via registry: `_registry.CreateWorkerAsync(workerId, workerFunc, ct)`
- Inject `IAutoscalingCoordinator<TWork>` or use inner orchestrator directly

### 7i. [RED → GREEN] Remove fallback constructor (M5) + use IOptions (L8)

- File: `Bifrost/Decorators/AutoscalingOrchestrator.cs`
- Delete secondary constructor at lines 86-94
- Change `AutoscalingOptions options` to `IOptions<AutoscalingOptions> options`
- Update `_options` references to `_options.Value` or store `.Value` in constructor
- Update tests to use primary constructor

### 7j. [RED] AutoscalingEngine — Track pending scaling tasks (M8)

Write tests:
- `ExecuteScalingDecisionAsync_TracksPendingTask`
- `StopAsync_DrainsPendingScalingTasks`
- `ExecuteScalingDecisionAsync_OnFault_LogsError`
- File: `Bifrost.Tests/Autoscaling/AutoscalingEngineTests.cs`
- Expected failure: No task tracking exists

### 7k. [GREEN] Add pending task tracking to AutoscalingEngine

- File: `Bifrost/Autoscaling/AutoscalingEngine.cs`
- Add `private readonly ConcurrentBag<Task> _pendingScalingTasks = new()`
- In `EvaluateScalingCallback`: store task in bag instead of discarding
- Add `ContinueWith(t => log if faulted)` to each task
- In `StopAsync`: `await Task.WhenAll(_pendingScalingTasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)`
- Add XML doc to `ScalingDecisionMade` event (M9)

### 7l. [RED] WorkerRegistry — Task fault tracking (M12)

Write tests:
- `CreateWorkerAsync_WorkerFaults_LogsError`
- File: `Bifrost.Tests/Autoscaling/WorkerRegistryTests.cs`
- Expected failure: Faulted tasks go unobserved

### 7m. [GREEN] Add ContinueWith for worker task faults

- File: `Bifrost/Autoscaling/WorkerRegistry.cs`
- Chain `.ContinueWith()` on the `Task.Factory.StartNew().Unwrap()` result
- Log error on fault state
- Optionally store task in `WorkerInfo` for external observation

### 7n. [RED → GREEN] Simplify Enabled conditional (L1)

- File: `Bifrost/Decorators/AutoscalingOrchestrator.cs`
- When `!_options.Enabled`: all methods delegate directly to inner without metrics/registry interaction
- Simplify conditional branches to early-return pattern

### 7o. [RED → GREEN] Update DI wiring (AutoscalingExtensions)

- File: `Bifrost/DependencyInjection/AutoscalingExtensions.cs`
- Wire `AutoscalingCoordinator<TWork>` with orchestrator, registry, and optional event stream
- Remove fallback constructor usage
- Use `IOptions<AutoscalingOptions>` pattern

### 7p. [REFACTOR] Clean up autoscaling subsystem

- Remove any dead code paths
- Ensure consistent logging patterns
- Verify L4 is resolved (dynamic workers now use CreateWorkerFunction which has built-in logging)

---

## Task 8: Documentation (M9, L5)

**Findings:** L5 (resilience fallback doc)
**Files:** `Bifrost.Resilience/ResiliencyPolicyGenerator.cs`
**Parallelizable:** No — Wave 3 (M9 folded into Task 7)
**Dependencies:** Task 7

### 8a. [GREEN] Document resilience fallback behavior (L5)

- File: `Bifrost.Resilience/ResiliencyPolicyGenerator.cs`
- Add XML doc to `CreatePolicy<T>` method explaining:
  - Fallback is outermost policy
  - Returns provided default value when all retries exhausted
  - Logs error via ILogger before returning fallback
  - Callers cannot distinguish "succeeded after retry" from "fell back to default"

---

## Task 9: Integration Test (L10)

**Findings:** L10 (decorator tests over-mock)
**Files:** New test file
**Parallelizable:** No — Final wave
**Dependencies:** All other tasks

### 9a. [RED] Multi-decorator integration test

Write test: `FullDecoratorChain_ProcessesWorkEndToEnd`
- File: `Bifrost.Tests/DependencyInjection/MultiDecoratorIntegrationTests.cs`
- Setup: Register via DI with real `WorkOrchestrator` + `EventStreamOrchestrator` + `AutoscalingOrchestrator`
- Test: Enqueue work item → verify handler called → verify events published → verify metrics tracked
- No mocking of inner orchestrator
- Expected failure: Test doesn't exist

### 9b. [GREEN] Implement integration test

- Use `ServiceCollection` with full registration chain
- Real handler that records invocations
- Subscribe to event stream, verify `WorkEnqueuedEvent` and `WorkCompletedEvent` received
- Assert `ActiveWorkers > 0`, `PendingCount` transitions

### 9c. [REFACTOR] Extract test helpers if needed

---

## Summary

| Task | Findings | Wave | Files | Estimated Steps |
|------|----------|------|-------|-----------------|
| 1 | M1, M11, L3 | 1 (parallel) | WorkerInfo, WorkOrchestrator | 7 |
| 2 | H3, M4, M6 | 1 (parallel) | DeadLetterQueue, DeadLetterNotifier, IDeadLetterNotifier, DeadLetterHandler, DLQ Extensions | 6 |
| 3 | M3, L2 | 1 (parallel) | ScalingAction (delete), ScalingEvent, AutoscalingOptions | 5 |
| 4 | M2, M7 | 1 (parallel) | WorkOrchestratorBuilder | 5 |
| 5 | M14, M15, L9 | 1 (parallel) | Contract test files | 6 |
| 6 | M10, M13, L6 | 1 (parallel) | EventStreamOrchestrator | 7 |
| 7 | H1, H2, H4, M5, M8, M9, M12, L1, L4, L8 | 2 (sequential) | AutoscalingCoordinator, AutoscalingOrchestrator, AutoscalingEngine, WorkerRegistry, AutoscalingExtensions | 16 |
| 8 | L5 | 3 (sequential) | ResiliencyPolicyGenerator | 1 |
| 9 | L10 | 3 (sequential) | New integration test | 3 |
| **Total** | **31 findings** (L7 false positive) | | **~23 files** | **56 steps** |

### Finding Coverage

All 32 audit findings addressed:
- **4 HIGH:** H1 (Task 7), H2 (Task 7), H3 (Task 2), H4 (Task 7)
- **17 MEDIUM:** M1 (Task 1), M2 (Task 4), M3 (Task 3), M4 (Task 2), M5 (Task 7), M6 (Task 2), M7 (Task 4), M8 (Task 7), M9 (Task 7), M10 (Task 6), M11 (Task 1), M12 (Task 7), M13 (Task 6), M14 (Task 5), M15 (Task 5)
- **10 LOW:** L1 (Task 7), L2 (Task 3), L3 (Task 1), L4 (Task 7), L5 (Task 8), L6 (Task 6), L7 (FALSE POSITIVE), L8 (Task 7), L9 (Task 5), L10 (Task 9)
