# Implementation Plan: Bifrost 0.4.0 — API Surface Improvements

**Design:** `docs/designs/2026-03-15-api-surface-improvements.md`
**Issue:** [#14](https://github.com/lvlup-sw/bifrost/issues/14)
**Iron Law:** No production code without a failing test first.

---

## Task Overview

| Group | Tasks | Parallelizable | Dependencies |
|-------|-------|----------------|-------------|
| A: Core Abstractions | 1–3 | Yes (within group) | None |
| B: Handler Registration & Scoping | 4–7 | Partially | Group A |
| C: DLQ Extensibility | 8–11 | Partially | Groups A, B |
| D: DrainAsync | 12–13 | No | Group A |
| E: Handler Decorator Extensibility | 14 | Yes | Group B |
| F: Documentation | 15–17 | Yes (within group) | None |
| G: Integration Tests | 18–20 | Yes (within group) | Groups B–E |

---

## Group A: Core Abstractions

### Task 1: IDeadLetterSubscriber\<TWork\> Interface
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write test: `IDeadLetterSubscriberTests.cs`
   - File: `src/Bifrost.Tests/DeadLetter/IDeadLetterSubscriberTests.cs`
   - Tests:
     - `IDeadLetterSubscriber_IsInterface` — verify type is interface
     - `IDeadLetterSubscriber_IsPublic` — verify public accessibility
     - `IDeadLetterSubscriber_HasHandleAsyncMethod` — verify method signature: `Task HandleAsync(DeadLetteredWork<TWork> item, CancellationToken ct)`
     - `IDeadLetterSubscriber_IsGeneric` — verify single generic type parameter
   - Expected failure: `IDeadLetterSubscriber<TWork>` type does not exist

2. **[GREEN]** Implement `IDeadLetterSubscriber<TWork>`
   - File: `src/Bifrost.Core/DeadLetter/IDeadLetterSubscriber.cs`
   - Public interface with single `HandleAsync` method

3. **[REFACTOR]** XML docs, copyright header

**Dependencies:** None
**Parallelizable:** Yes

---

### Task 2: InlineDelegateWorkHandler\<TWork\>
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write test: `InlineDelegateWorkHandlerTests.cs`
   - File: `src/Bifrost.Tests/Handlers/InlineDelegateWorkHandlerTests.cs`
   - Tests:
     - `HandleAsync_InvokesDelegate` — verify delegate is called with work and cancellation token
     - `HandleAsync_PropagatesException` — verify exceptions from delegate propagate
     - `HandleAsync_PassesCancellationToken` — verify ct is forwarded
     - `Constructor_ThrowsWhenDelegateNull` — verify null guard
     - `ImplementsIWorkHandler` — verify implements `IWorkHandler<TWork>`
   - Expected failure: `InlineDelegateWorkHandler<TWork>` type does not exist

2. **[GREEN]** Implement `InlineDelegateWorkHandler<TWork>`
   - File: `src/Bifrost/Handlers/InlineDelegateWorkHandler.cs`
   - Internal sealed class wrapping `Func<TWork, CancellationToken, ValueTask>`

3. **[REFACTOR]** XML docs

**Dependencies:** None
**Parallelizable:** Yes

---

### Task 3: ScopedHandlerProxy\<TWork\>
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write test: `ScopedHandlerProxyTests.cs`
   - File: `src/Bifrost.Tests/Handlers/ScopedHandlerProxyTests.cs`
   - Tests:
     - `HandleAsync_CreatesScope` — verify `IServiceScopeFactory.CreateAsyncScope()` is called
     - `HandleAsync_ResolvesHandlerFromScope` — verify `IWorkHandler<TWork>` resolved from scope's ServiceProvider
     - `HandleAsync_DisposesScope` — verify scope is disposed after handler completes
     - `HandleAsync_DisposesScope_OnException` — verify scope disposed even when handler throws
     - `HandleAsync_FreshScopePerCall` — call twice, verify two separate scopes created
     - `HandleAsync_PassesWorkAndCancellationToken` — verify args forwarded to resolved handler
     - `Constructor_ThrowsWhenScopeFactoryNull` — verify null guard
   - Expected failure: `ScopedHandlerProxy<TWork>` type does not exist

2. **[GREEN]** Implement `ScopedHandlerProxy<TWork>`
   - File: `src/Bifrost/Handlers/ScopedHandlerProxy.cs`
   - Internal sealed class: takes `IServiceScopeFactory`, creates async scope per `HandleAsync`, resolves and delegates

3. **[REFACTOR]** XML docs

**Dependencies:** None
**Parallelizable:** Yes

---

## Group B: Handler Registration & Scoping

### Task 4: Builder Infrastructure Changes
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write tests: `WorkOrchestratorBuilderTests.cs` (additions)
   - File: `src/Bifrost.Tests/DependencyInjection/WorkOrchestratorBuilderTests.cs`
   - Tests:
     - `Build_WithoutHandlerRegistered_FallsBackToServiceProvider` — verify existing behavior (backward compat): manually registered `IWorkHandler<T>` still works
     - `Build_WithScopedHandlerLifetime_UsesScopedHandlerProxy` — verify when `HandlerLifetime` is `Scoped`, the handler chain uses scope-per-item resolution
     - `PostBuildActions_ExecutedDuringBuild` — verify post-build actions run after orchestrator factory resolution
   - Expected failure: `HandlerLifetime`, `PostBuildActions` properties do not exist

2. **[GREEN]** Modify `WorkOrchestratorBuilder<TWork>`
   - File: `src/Bifrost/DependencyInjection/WorkOrchestratorBuilder.cs`
   - Add internal properties: `HandlerRegistered` (bool), `HandlerLifetime` (ServiceLifetime?), `EventPublishCallback` (Action<IOrchestratorEvent>?), `PostBuildActions` (List<Action<IServiceProvider>>)
   - Modify `Build()`: check `HandlerLifetime == Scoped` → use `ScopedHandlerProxy`, execute `PostBuildActions` after orchestrator construction

3. **[REFACTOR]** Clean up, verify existing tests still pass

**Dependencies:** Task 3
**Parallelizable:** No (modifies shared builder file)

---

### Task 5: WithHandler\<T\>() — Type-Based Overload
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write test: `HandlerExtensionsTests.cs`
   - File: `src/Bifrost.Tests/DependencyInjection/HandlerExtensionsTests.cs`
   - Tests:
     - `WithHandler_TypeBased_RegistersHandlerInDI` — verify `IWorkHandler<T>` is resolvable from built service provider
     - `WithHandler_TypeBased_DefaultLifetime_IsSingleton` — verify singleton registration
     - `WithHandler_TypeBased_ScopedLifetime_RegistersAsScoped` — verify scoped registration
     - `WithHandler_TypeBased_TransientLifetime_RegistersAsTransient` — verify transient registration
     - `WithHandler_TypeBased_SetsHandlerRegisteredFlag` — verify builder flag set
     - `WithHandler_CalledTwice_ThrowsInvalidOperationException` — verify double-call error
     - `WithHandler_TypeBased_NullBuilder_ThrowsArgumentNullException` — null guard
     - `WithHandler_TypeBased_ChainsWithOtherExtensions` — verify returns builder for chaining
   - Expected failure: `WithHandler` extension method does not exist

2. **[GREEN]** Implement type-based `WithHandler`
   - File: `src/Bifrost/DependencyInjection/HandlerExtensions.cs`
   - `WithHandler<TWork, THandler>(builder, lifetime)` — registers `THandler` as `IWorkHandler<TWork>` at specified lifetime, sets builder flags

3. **[REFACTOR]** Extract shared validation to private helper

**Dependencies:** Task 4
**Parallelizable:** No (depends on Task 4)

---

### Task 6: WithHandler\<T\>() — Factory and Delegate Overloads
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write tests (additions to `HandlerExtensionsTests.cs`)
   - File: `src/Bifrost.Tests/DependencyInjection/HandlerExtensionsTests.cs`
   - Tests:
     - `WithHandler_Factory_RegistersFromFactory` — verify factory-created handler is used
     - `WithHandler_Factory_RespectsLifetime` — verify lifetime passed through
     - `WithHandler_Factory_NullFactory_ThrowsArgumentNullException` — null guard
     - `WithHandler_Delegate_RegistersInlineDelegateHandler` — verify delegate wrapped in `InlineDelegateWorkHandler`
     - `WithHandler_Delegate_HandlerInvokesDelegateOnProcess` — end-to-end: enqueue work, verify delegate called
     - `WithHandler_Delegate_NullDelegate_ThrowsArgumentNullException` — null guard
     - `WithHandler_Delegate_RegistersAsSingleton` — verify default singleton lifetime
   - Expected failure: Factory/delegate overloads do not exist

2. **[GREEN]** Implement factory and delegate overloads
   - File: `src/Bifrost/DependencyInjection/HandlerExtensions.cs`
   - `WithHandler(builder, factory, lifetime)` — registers via factory
   - `WithHandler(builder, Func<TWork, CancellationToken, ValueTask>)` — wraps in `InlineDelegateWorkHandler`

3. **[REFACTOR]** Consolidate shared registration logic

**Dependencies:** Tasks 2, 5
**Parallelizable:** No (depends on Task 5)

---

### Task 7: Scoped Handler Integration
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write test: `ScopedHandlerIntegrationTests.cs`
   - File: `src/Bifrost.Tests/DependencyInjection/ScopedHandlerIntegrationTests.cs`
   - Tests:
     - `WithHandler_Scoped_ResolvesFromScope_PerWorkItem` — build full orchestrator with scoped handler, enqueue 3 items, verify 3 distinct handler instances used (use a tracking handler that records its instance ID)
     - `WithHandler_Scoped_WorksWithDeadLetterQueue` — scoped handler + DLQ: verify each retry creates fresh scope
     - `WithHandler_Scoped_WorksWithEventStream` — scoped handler + event stream: verify events published
     - `WithHandler_Singleton_ResolvesOnce` — verify singleton handler resolved once (control test)
   - Expected failure: Scope-per-item resolution not wired

2. **[GREEN]** Wire end-to-end (should already work from Tasks 3–6, this task verifies integration)

3. **[REFACTOR]** None expected

**Dependencies:** Tasks 5, 6
**Parallelizable:** No (integration, depends on all of Group B)

---

## Group C: DLQ Extensibility

### Task 8: DeadLetterNotifier Multi-Subscriber Refactor
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write tests (update `DeadLetterNotifierTests.cs`)
   - File: `src/Bifrost.Tests/DeadLetter/DeadLetterNotifierTests.cs`
   - New/modified tests:
     - `Notify_WithMultipleSubscribers_InvokesAllCallbacks` — register 3 subscribers, verify all called
     - `Subscribe_Multiple_DoesNotThrow` — replaces `Subscribe_WhenAlreadySubscribed_ThrowsInvalidOperationException` (was single-subscriber, now multi)
     - `Notify_SubscriberThrows_OtherSubscribersStillCalled` — error isolation: one throws, others still invoked
     - `Notify_SubscriberThrows_DoesNotPropagateToCallerSynchronously` — verify no exception escapes `Notify`
     - Existing tests must still pass (backward compat for single subscriber usage)
   - Expected failure: Second `Subscribe` call still throws

2. **[GREEN]** Refactor `DeadLetterNotifier<TWork>` to multi-subscriber
   - File: `src/Bifrost/DeadLetter/DeadLetterNotifier.cs`
   - Replace single `Action<>?` with `List<Func<WorkDeadLetteredEvent<TWork>, Task>>`
   - Thread-safe subscriber list with lock
   - Fire-and-forget with error swallowing per subscriber
   - Change `Subscribe` to accept `Func<WorkDeadLetteredEvent<TWork>, Task>` (async-capable)

3. **[REFACTOR]** Remove old single-subscriber `Subscription` class, clean up

**Dependencies:** None (internal refactor)
**Parallelizable:** Yes (modifies only DeadLetterNotifier.cs and its test)

---

### Task 9: DeadLetterHandler Event Publish Callback
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write tests (additions to `DeadLetterHandlerTests.cs`)
   - File: `src/Bifrost.Tests/DeadLetter/DeadLetterHandlerTests.cs`
   - Tests:
     - `HandleAsync_Failure_WithPublishCallback_PublishesEvent` — verify callback invoked with `WorkDeadLetteredEvent` after dead-lettering
     - `HandleAsync_Failure_WithNullPublishCallback_StillDeadLetters` — verify null callback doesn't break flow
     - `HandleAsync_Success_PublishCallbackNotInvoked` — verify callback not called on success
     - `Constructor_AcceptsNullPublishCallback` — verify optional parameter works
   - Expected failure: `DeadLetterHandler` constructor doesn't accept publish callback

2. **[GREEN]** Modify `DeadLetterHandler<TWork>`
   - File: `src/Bifrost/DeadLetter/DeadLetterHandler.cs`
   - Add optional `Action<IOrchestratorEvent>? eventPublishCallback = null` constructor parameter
   - In dead-letter path: call `_eventPublishCallback?.Invoke(evt)` after `_notifier.Notify(evt)`

3. **[REFACTOR]** Update existing `CreateHandler()` test helper

**Dependencies:** None (can be done in parallel with Task 8)
**Parallelizable:** Yes

---

### Task 10: EventStream + DLQ Wiring
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write tests: `DeadLetterEventStreamIntegrationTests.cs`
   - File: `src/Bifrost.Tests/DependencyInjection/DeadLetterEventStreamIntegrationTests.cs`
   - Tests:
     - `WithEventStream_AndWithDeadLetterQueue_PublishesWorkDeadLetteredEvent` — build orchestrator with both, trigger dead-letter, verify event appears in event stream
     - `WithDeadLetterQueue_AndWithEventStream_OrderIndependent` — call in reverse order, verify same behavior
     - `WithDeadLetterQueue_WithoutEventStream_NoPublishCallback` — verify DLQ works alone (no event stream wiring)
     - `WithEventStream_WithoutDeadLetterQueue_NoError` — verify event stream works alone
   - Expected failure: `EventPublishCallback` not set, `DeadLetterHandler` not passed callback

2. **[GREEN]** Wire `EventPublishCallback` in extensions
   - File: `src/Bifrost/DependencyInjection/EventStreamExtensions.cs` — set `builder.EventPublishCallback`
   - File: `src/Bifrost/DependencyInjection/DeadLetterQueueExtensions.cs` — pass `builder.EventPublishCallback` to `DeadLetterHandler` factory

3. **[REFACTOR]** Clean up

**Dependencies:** Tasks 4, 9
**Parallelizable:** No (modifies builder infrastructure)

---

### Task 11: DeadLetterSubscriberExtensions
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write test: `DeadLetterSubscriberExtensionsTests.cs`
   - File: `src/Bifrost.Tests/DependencyInjection/DeadLetterSubscriberExtensionsTests.cs`
   - Tests:
     - `WithDeadLetterSubscriber_TypeBased_RegistersSubscriber` — verify subscriber type registered in DI
     - `WithDeadLetterSubscriber_TypeBased_InvokedOnDeadLetter` — build full orchestrator, trigger dead-letter, verify subscriber `HandleAsync` called with correct `DeadLetteredWork`
     - `WithDeadLetterSubscriber_Callback_InvokedOnDeadLetter` — build with callback, trigger dead-letter, verify callback called
     - `WithDeadLetterSubscriber_Multiple_AllInvoked` — register interface + callback, both called
     - `WithDeadLetterSubscriber_WithoutWithDeadLetterQueue_ThrowsOnBuild` — verify requires DLQ configuration (or document graceful no-op)
     - `WithDeadLetterSubscriber_NullBuilder_ThrowsArgumentNullException` — null guard
     - `WithDeadLetterSubscriber_NullCallback_ThrowsArgumentNullException` — null guard
   - Expected failure: `WithDeadLetterSubscriber` does not exist

2. **[GREEN]** Implement `DeadLetterSubscriberExtensions`
   - File: `src/Bifrost/DependencyInjection/DeadLetterSubscriberExtensions.cs`
   - `WithDeadLetterSubscriber<TWork, TSubscriber>()` — registers subscriber, adds post-build action to wire into notifier
   - `WithDeadLetterSubscriber<TWork>(callback)` — adds post-build action to wire callback into notifier

3. **[REFACTOR]** XML docs

**Dependencies:** Tasks 1, 4, 8
**Parallelizable:** No (depends on notifier refactor + builder infrastructure)

---

## Group D: DrainAsync

### Task 12: DrainAsync Interface + Base Implementation
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write tests
   - File: `src/Bifrost.Tests/Contracts/IWorkOrchestratorTests.cs` (additions)
   - Tests:
     - `DrainAsync_HasCorrectSignature` — verify `Task DrainAsync(CancellationToken ct)` exists on interface
     - `Interface_HasExpectedMemberCount` — update count from 18 to 20 (DrainAsync method + param)
   - File: `src/Bifrost.Tests/WorkOrchestratorDrainTests.cs` (new)
   - Tests:
     - `DrainAsync_ProcessesRemainingItems` — enqueue items, call DrainAsync, verify all processed
     - `DrainAsync_RejectsNewEnqueues` — after DrainAsync starts, `TryEnqueue` returns false
     - `DrainAsync_EmptyQueue_CompletesImmediately` — drain on empty orchestrator returns quickly
     - `DrainAsync_WithCancellation_ThrowsOperationCanceled` — verify ct is honored
     - `DrainAsync_PendingCountIsZero_AfterDrain` — verify all items consumed
   - Expected failure: `DrainAsync` method does not exist on interface

2. **[GREEN]** Add `DrainAsync` to interface and base implementation
   - File: `src/Bifrost.Core/IWorkOrchestrator.cs` — add method signature
   - File: `src/Bifrost/WorkOrchestrator.cs` — implement: `_channel.Writer.TryComplete()` + `await Task.WhenAll(_workers).WaitAsync(ct)`

3. **[REFACTOR]** XML docs on interface

**Dependencies:** None
**Parallelizable:** Yes (separate files from Groups B/C)

---

### Task 13: DrainAsync Decorator Passthroughs
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write tests
   - File: `src/Bifrost.Tests/Resilience/ResilientOrchestratorTests.cs` (addition)
   - Tests:
     - `DrainAsync_ForwardsToInner` — verify ResilientOrchestrator delegates to inner
   - File: `src/Bifrost.Tests/Decorators/EventStreamOrchestratorTests.cs` (addition)
   - Tests:
     - `DrainAsync_ForwardsToInner_AndCompletesSubscribers` — verify inner drain + subscriber channels completed
   - File: `src/Bifrost.Tests/Decorators/AutoscalingOrchestratorTests.cs` (addition)
   - Tests:
     - `DrainAsync_ForwardsToInner` — verify AutoscalingOrchestrator delegates to inner
   - Expected failure: Decorators don't implement `DrainAsync`

2. **[GREEN]** Implement `DrainAsync` on all decorators
   - File: `src/Bifrost.Resilience/ResilientOrchestrator.cs` — passthrough
   - File: `src/Bifrost/Decorators/EventStreamOrchestrator.cs` — drain inner + complete subscriber channels
   - File: `src/Bifrost/Decorators/AutoscalingOrchestrator.cs` — drain inner

3. **[REFACTOR]** None expected

**Dependencies:** Task 12
**Parallelizable:** No (sequentially depends on Task 12)

---

## Group E: Handler Decorator Extensibility

### Task 14: WithHandlerDecorator Extension
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Write test: `HandlerDecoratorExtensionsTests.cs`
   - File: `src/Bifrost.Tests/DependencyInjection/HandlerDecoratorExtensionsTests.cs`
   - Tests:
     - `WithHandlerDecorator_RegistersDecorator` — verify decorator appears in builder's HandlerDecorators list
     - `WithHandlerDecorator_AutoAssignsOrder` — verify auto-incrementing order when not specified
     - `WithHandlerDecorator_ExplicitOrder_UsesProvidedOrder` — verify explicit order honored
     - `WithHandlerDecorator_DecoratorWrapsHandler` — build orchestrator, verify decorator wraps the handler (e.g., logging decorator that records calls)
     - `WithHandlerDecorator_ComposesWithBuiltInDecorators` — verify works alongside WithDeadLetterQueue handler decorator
     - `WithHandlerDecorator_NullFactory_ThrowsArgumentNullException` — null guard
     - `WithHandlerDecorator_NullBuilder_ThrowsArgumentNullException` — null guard
     - `WithHandlerDecorator_ReturnsBuilderForChaining` — verify method chaining
   - Expected failure: `WithHandlerDecorator` extension method does not exist

2. **[GREEN]** Implement `HandlerDecoratorExtensions`
   - File: `src/Bifrost/DependencyInjection/HandlerDecoratorExtensions.cs`
   - Public extension method on `WorkOrchestratorBuilder<TWork>`, adds to `HandlerDecorators` list

3. **[REFACTOR]** XML docs

**Dependencies:** Task 4
**Parallelizable:** Yes (can run alongside Group C/D if builder changes from Task 4 are available)

---

## Group F: Documentation (XML Docs)

### Task 15: Sync Resilience Bypass Documentation
**Phase:** RED → GREEN

1. **[RED]** Write test: additions to `ResilientOrchestratorTests.cs`
   - File: `src/Bifrost.Tests/Resilience/ResilientOrchestratorTests.cs`
   - Tests:
     - `TryEnqueue_HasXmlRemarks_DocumentingSyncBypass` — reflection test: verify the `TryEnqueue` method has `<remarks>` XML documentation (check via source or doc-comment attribute convention, or test that behavior matches docs: `TryEnqueue` does NOT go through policy)
     - `TryRun_BypassesResiliencePolicy` — verify `TryRun` directly delegates without policy wrapping (assert inner called directly)
     - `Run_BypassesResiliencePolicy` — verify `Run` directly delegates without policy wrapping
   - Expected failure: Tests for `TryRun`/`Run` bypass may not exist

2. **[GREEN]** Add XML documentation
   - File: `src/Bifrost.Resilience/ResilientOrchestrator.cs` — add `<remarks>` to `TryEnqueue`, `TryRun`, `Run` explaining sync bypass rationale

**Dependencies:** None
**Parallelizable:** Yes

---

### Task 16: MaxRetries Semantics Documentation
**Phase:** RED → GREEN

1. **[RED]** Write test: addition to `DeadLetterQueueOptionsTests.cs`
   - File: `src/Bifrost.Tests/DeadLetter/DeadLetterQueueOptionsTests.cs`
   - Tests:
     - `MaxRetries_XmlDoc_ClarifiesTotalAttempts` — test that documents the formula: with MaxRetries=3, total attempts = 4 (verify via DeadLetterHandler behavior, which already exists in `HandleAsync_Failure_RetriesUpToMaxRetries`)
   - Note: This is mostly a documentation task. The behavioral tests already exist. Add a descriptive test that makes the semantics explicit.

2. **[GREEN]** Enhance XML documentation
   - File: `src/Bifrost.Core/DeadLetter/DeadLetterQueueOptions.cs` — expand `<remarks>` on `MaxRetries` with formula and examples

**Dependencies:** None
**Parallelizable:** Yes

---

### Task 17: Resilience vs DLQ Interaction Documentation
**Phase:** RED → GREEN

1. **[RED]** Write test: `ResilienceDlqInteractionTests.cs`
   - File: `src/Bifrost.Tests/Resilience/ResilienceDlqInteractionTests.cs`
   - Tests:
     - `Resilience_WrapsEnqueue_DLQ_WrapsHandler_IndependentLayers` — build orchestrator with both `.WithResilience()` and `.WithDeadLetterQueue()`, verify resilience applies to enqueue (enqueue failure triggers policy retry), DLQ applies to handler (handler failure triggers DLQ retry), and they don't compound
     - `Resilience_DoesNotRetryHandlerFailures` — verify handler exceptions don't trigger resilience policy
     - `DLQ_DoesNotRetryEnqueueFailures` — verify enqueue failures don't trigger DLQ retry
   - Expected failure: Tests may pass (behavior exists), but test names document the interaction explicitly

2. **[GREEN]** Add XML documentation
   - File: `src/Bifrost.Core/DeadLetter/DeadLetterQueueOptions.cs` — add class-level `<remarks>` about two-layer retry model
   - File: `src/Bifrost.Resilience/ResiliencySettings.cs` — add `<remarks>` about enqueue-only scope

**Dependencies:** None
**Parallelizable:** Yes

---

## Group G: Integration Tests

### Task 18: Full Builder Chain Integration Test
**Phase:** RED → GREEN

1. **[RED]** Write test: `FullBuilderChainIntegrationTests.cs`
   - File: `src/Bifrost.Tests/DependencyInjection/FullBuilderChainIntegrationTests.cs`
   - Tests:
     - `Builder_AllExtensions_ChainableInAnyOrder` — verify `.WithHandler().WithDeadLetterQueue().WithDeadLetterSubscriber().WithEventStream().WithHandlerDecorator().WithResilience().WithAutoscaling().Build()` succeeds
     - `Builder_AllExtensions_ReverseOrder_ChainableInAnyOrder` — verify reverse call order also works
     - `Builder_FullChain_ProcessesWorkEndToEnd` — enqueue work, verify handler called, events published, DLQ subscriber wired
     - `Builder_WithHandler_BackwardCompatible_ManualRegistration` — verify old pattern (manual DI + builder without `WithHandler`) still works

2. **[GREEN]** Fix any integration issues discovered

**Dependencies:** All previous groups
**Parallelizable:** No (final integration)

---

### Task 19: Scoped Handler E2E Integration
**Phase:** RED → GREEN

1. **[RED]** Write test (merged into Task 7)
   - Already covered by Task 7 (`ScopedHandlerIntegrationTests.cs`)

**Status:** Covered by Task 7
**Dependencies:** Task 7
**Parallelizable:** N/A

---

### Task 20: Update Existing Interface Contract Test
**Phase:** RED → GREEN

1. **[RED]** Update `IWorkOrchestratorTests.cs`
   - File: `src/Bifrost.Tests/Contracts/IWorkOrchestratorTests.cs`
   - Tests:
     - Update `Interface_HasExpectedMemberCount` — change from 18 to 20 (adding DrainAsync method + CancellationToken parameter)
   - Note: This is a bookkeeping task that should be done as part of Task 12. Listed separately for visibility.

**Status:** Incorporated into Task 12
**Dependencies:** Task 12

---

## Parallelization Map

```
Time →
─────────────────────────────────────────────────────────────────────

Stream 1:  [Task 1] ──┐
                       ├──→ [Task 11] (needs 1, 4, 8)
Stream 2:  [Task 2] ──┤
                       │
Stream 3:  [Task 3] ──┤
                       │
Stream 4:  [Task 8] ──┘    (needs nothing, standalone refactor)
                       │
                       ↓
           [Task 4]  ←── builder infrastructure (needs Task 3)
                |
         ┌──────┼──────┬──────────┐
         ↓      ↓      ↓          ↓
     [Task 5] [Task 9] [Task 14] [Task 12]
         |      |                     |
         ↓      ↓                     ↓
     [Task 6] [Task 10]          [Task 13]
         |      |
         ↓      ↓
     [Task 7] [Task 11]
                |
                ↓
           [Task 18] (final integration)

Stream 5:  [Task 15] ─┐
           [Task 16] ──┤── (all independent, run anytime)
           [Task 17] ──┘
```

**Delegation groups for worktree parallelism:**

| Worktree | Tasks | Files Modified |
|----------|-------|----------------|
| A (core abstractions) | 1, 2, 3 | New files only (Bifrost.Core + Bifrost + Tests) |
| B (handler registration) | 4, 5, 6, 7 | WorkOrchestratorBuilder.cs, new HandlerExtensions.cs, new tests |
| C (DLQ extensibility) | 8, 9, 10, 11 | DeadLetterNotifier.cs, DeadLetterHandler.cs, DLQ/EventStream extensions, new tests |
| D (DrainAsync) | 12, 13 | IWorkOrchestrator.cs, WorkOrchestrator.cs, all decorators, new tests |
| E (decorator + docs) | 14, 15, 16, 17 | New HandlerDecoratorExtensions.cs, ResilientOrchestrator.cs XML, DeadLetterQueueOptions.cs XML |
| F (integration) | 18 | New integration test files only |

**Safe parallel worktrees:** A + D + E can run simultaneously (non-overlapping files).
**Sequential:** B depends on A. C depends on A + B. F depends on all.
