# Quality Audit Remediation

**Date:** 2026-03-14
**Issue:** #11 — Backend quality audit: 4 HIGH, 17 MEDIUM findings across 7 dimensions
**Scope:** All 32 findings (4 HIGH, 17 MEDIUM, 10 LOW + 1 false positive)
**Approach:** Complete the Coordinator Pattern + holistic hardening
**Delivery:** Single PR

---

## 1. Executive Summary

This design addresses all findings from the axiom:audit report (issue #11). The primary architectural work is completing the autoscaling subsystem — making dynamic worker scaling fully functional by implementing the 8 stub methods in `AutoscalingCoordinator`, fixing the disconnected channel reader, and removing vestigial code. Secondary work hardens shutdown paths, adds DLQ observability, consolidates duplicates, and completes contract test coverage.

---

## 2. Autoscaling Completion (H1, H2, H4, M5, M8, M12, L1, L4, L8)

### 2.1 Problem

The autoscaling subsystem is architecturally sound but incomplete. The coordinator pattern (hexagonal ports) was designed to resolve circular dependencies between the engine, orchestrator, and registry. However, only the metrics port is implemented — the control and events ports throw `NotImplementedException`. Additionally, `AsChannelReader()` returns a disconnected empty channel, making dynamic workers unable to receive work.

### 2.2 Design

**Complete `AutoscalingCoordinator` by delegating to existing components:**

The coordinator already holds a reference to `IWorkOrchestrator<TWork>`. It needs additional references to the `EventStreamOrchestrator` (for event publishing) via interface. The control port delegates to the inner orchestrator and registry:

```
IAutoscalingControlPort:
  CreateWorkerFunction()       → _innerOrchestrator.CreateWorkerFunction()
  CreateWorkerFunction(cb)     → _innerOrchestrator.CreateWorkerFunction(cb)
  RequestScaleUpAsync(n, ct)   → create n workers via registry using above function
  RequestScaleDownAsync(n, ct) → _registry.RequestMultipleWorkerStop(n)
  GetShutdownToken()           → _innerOrchestrator.GetShutdownToken()

IAutoscalingEventsPort:
  PublishEvent(evt)            → _eventStreamOrchestrator?.PublishToSubscribers(evt)
  QueuedEventCount             → _eventStreamOrchestrator?.SubscriberCount ?? 0
  ActiveSubscriberCount        → _eventStreamOrchestrator?.SubscriberCount ?? 0
```

**Fix the channel reader disconnect (H1):**

Delete `AsChannelReader()` entirely. Dynamic workers created via `CreateWorkerFunction()` already read from the real channel — the inner orchestrator's `CreateWorkerFunction()` captures the channel reader in its closure. The `AutoscalingOrchestrator.RequestScaleUpAsync` method should use `_coordinator.CreateWorkerFunction(stateCallback)` instead of manually constructing a worker loop with the broken `AsChannelReader()`.

**Remove empty try block (H4):**

The empty try block at `AutoscalingOrchestrator:174-178` exists because the manual worker loop was a placeholder. With workers created via `CreateWorkerFunction()`, this entire manual loop is replaced by the registry's `AddWorker()` call with the coordinator-provided function.

**Remove fallback constructor (M5):**

Delete the `AutoscalingOrchestrator(inner, metrics)` constructor that bypasses DI validation. All construction goes through the primary constructor with proper validation.

**Fix fire-and-forget scaling execution (M8):**

Track pending scaling tasks in `AutoscalingEngine`. Add a `ConcurrentBag<Task>` for in-flight scaling operations. Observe faults via `ContinueWith()` logging. Drain pending tasks in `StopAsync()`.

**Fix worker startup task tracking (M12):**

In `WorkerRegistry.AddWorker()`, chain `.ContinueWith()` to the started task to log faults. Store task reference in `WorkerInfo` for observation.

**Fix bare options (L8):**

Change `AutoscalingOrchestrator` constructor to accept `IOptions<AutoscalingOptions>` instead of bare `AutoscalingOptions`.

**Fix unlogged dynamic worker exceptions (L4):**

With workers now using `CreateWorkerFunction()`, they inherit the base orchestrator's exception logging. No separate fix needed.

**Simplify Enabled conditional (L1):**

Keep `AutoscalingOptions.Enabled` but simplify — when disabled, the decorator becomes a pure pass-through (no metrics tracking, no conditional branches). Document that disabling autoscaling means the decorator is a no-op wrapper.

### 2.3 Dependency Injection Changes

`AutoscalingExtensions.cs` must wire the coordinator with all required dependencies:

```csharp
services.AddSingleton<IAutoscalingCoordinator<TWork>>(sp =>
{
    var orchestrator = sp.GetRequiredService<IWorkOrchestrator<TWork>>();
    var registry = sp.GetRequiredService<IWorkerRegistry>();
    var eventStream = sp.GetService<IEventStreamOrchestrator<TWork>>(); // optional
    return new AutoscalingCoordinator<TWork>(orchestrator, registry, eventStream);
});
```

Note: `IEventStreamOrchestrator` is optional — autoscaling works without event streaming.

---

## 3. DLQ Observability (H3, M6, M4)

### 3.1 Problem

The DLQ uses `BoundedChannelFullMode.DropOldest` with zero observability. When capacity is exceeded, items are silently lost. The notifier silently replaces subscribers. The `IDeadLetterNotifier` interface has a single sealed implementation adding unnecessary indirection.

### 3.2 Design

**Add drop counter (H3):**

Add a `DroppedCount` property (backed by `Interlocked.Increment`) to `DeadLetterQueue<TWork>`. Before writing to the channel, check if `_channel.Reader.Count >= capacity` and if so, increment the counter and log a warning:

```csharp
public long DroppedCount => Volatile.Read(ref _droppedCount);

public async ValueTask EnqueueAsync(DeadLetteredWork<TWork> item, CancellationToken ct)
{
    if (_channel.Reader.CanCount && _channel.Reader.Count >= _capacity)
    {
        Interlocked.Increment(ref _droppedCount);
        _logger.LogWarning("DLQ at capacity ({Capacity}), oldest item will be dropped", _capacity);
    }
    await _channel.Writer.WriteAsync(item, ct).ConfigureAwait(false);
}
```

This requires injecting `ILogger<DeadLetterQueue<TWork>>` — add it to the constructor and DI registration.

**Throw on duplicate subscription (M6):**

Change `DeadLetterNotifier.Subscribe()` to throw `InvalidOperationException` if `_callback` is already set:

```csharp
public IDisposable Subscribe(Action<WorkDeadLetteredEvent<TWork>> callback)
{
    if (Interlocked.CompareExchange(ref _callback, callback, null) != null)
        throw new InvalidOperationException("DeadLetterNotifier already has an active subscriber.");
    return new Subscription(this);
}
```

**Remove IDeadLetterNotifier interface (M4):**

Replace `IDeadLetterNotifier<TWork>` usage with the concrete `DeadLetterNotifier<TWork>` type. It's `internal sealed` with one implementation — the interface adds indirection without extensibility. Update DI registrations and consuming types.

---

## 4. Concurrency & Shutdown Hardening (M1, M11, M9)

### 4.1 Problem

`WorkerInfo._stopRequested` uses `volatile bool` while `_isIdle` uses `Interlocked` — inconsistent synchronization. `DisposeAsync` has no timeout (can hang). `ScalingDecisionMade` handler exceptions are caught but behavior is undocumented.

### 4.2 Design

**Fix volatile bool (M1):**

Change `_stopRequested` from `volatile bool` to `int` with `Interlocked.Exchange`/`Volatile.Read`, matching `_isIdle`:

```csharp
private int _stopRequested; // 1 = stop requested, 0 = running

public bool StopRequested => Volatile.Read(ref _stopRequested) == 1;
public void RequestStop() => Interlocked.Exchange(ref _stopRequested, 1);
```

**Fix DisposeAsync timeout (M11):**

Reuse `StopAsync` logic. If already stopped, just dispose. Otherwise, apply a 5-second timeout:

```csharp
public async ValueTask DisposeAsync()
{
    _channel.Writer.TryComplete();
    await _cts.CancelAsync().ConfigureAwait(false);

    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try
    {
        await Task.WhenAll(_workers)
            .WaitAsync(timeoutCts.Token)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
    catch (OperationCanceledException)
    {
        _logger.LogWarning("DisposeAsync timed out waiting for workers");
    }

    _cts.Dispose();
}
```

**Document event handler contract (M9):**

Add XML doc to `ScalingDecisionMade` event warning that handler exceptions are caught and logged but do not propagate. No code change beyond documentation.

---

## 5. Type Consolidation & Cleanup (M3, L6, L7, L2, L5)

### 5.1 Design

**Consolidate ScalingAction enum (M3):**

Delete `Bifrost/Autoscaling/ScalingAction.cs`. Update all references in the autoscaling namespace to use `Bifrost.Core.Events.ScalingAction`. Add explicit values to the Core enum to match:

```csharp
// Bifrost.Core/Events/ScalingEvent.cs
public enum ScalingAction
{
    None = 0,
    ScaleUp = 1,
    ScaleDown = 2
}
```

**Publish WorkCompletedEvent (L6):**

Wire `EventStreamOrchestrator` to publish `WorkCompletedEvent<TWork>` when a work item completes. This requires the decorated handler to report completion. Add publishing in the worker loop after successful handler execution:

```csharp
PublishToSubscribers(new WorkCompletedEvent<TWork>(work, elapsed, success: true));
```

On failure (before dead-lettering), publish with `success: false`.

**Remove unused helper — FALSE POSITIVE (L7):**

`CreateAutoscalingData` in `AutoscalingHealthCheck.cs` IS used at line 77. No change needed. Close finding.

**Make AutoscalingOptions immutable after construction (L2):**

Change setters to `init` accessors. This preserves the Options Pattern binding while preventing runtime mutation:

```csharp
public class AutoscalingOptions
{
    public bool Enabled { get; init; } = true;
    public int MinWorkers { get; init; } = 1;
    // ...
}
```

**Document resilience fallback behavior (L5):**

Add XML doc to `ResiliencyPolicyGenerator` methods documenting that fallback returns default value silently after all retries exhausted. The logging already exists — this is a documentation gap, not a code gap.

---

## 6. Decorator Ordering & DI Validation (M2, M7)

### 6.1 Design

**Validate unique decorator orders (M2):**

In `WorkOrchestratorBuilder.Build()`, before sorting, check for duplicate `Order` values:

```csharp
var duplicateOrders = Decorators
    .GroupBy(d => d.Order)
    .Where(g => g.Count() > 1)
    .Select(g => g.Key)
    .ToList();

if (duplicateOrders.Count > 0)
    throw new InvalidOperationException(
        $"Duplicate decorator order(s): {string.Join(", ", duplicateOrders)}");
```

**Add Order to handler decorators (M7):**

Add an `Order` property to handler decorator registrations matching the orchestrator decorator pattern. Apply the same uniqueness validation.

---

## 7. Event Stream Hardening (M10, M13)

### 7.1 Design

**Add subscriber timeout cleanup (M10):**

Add a periodic cleanup sweep (piggyback on existing operations, no separate timer). When `PublishToSubscribers` runs, check subscriber ages. If a subscriber channel has been open longer than a configurable timeout (default: 5 minutes) with no reads, mark it for removal:

```csharp
// In PublishToSubscribers, after writing:
foreach (var (id, channel) in _eventSubscribers)
{
    if (channel.Reader.CanCount && channel.Reader.Count >= _subscriberCapacity)
    {
        _eventSubscribers.TryRemove(id, out _);
        channel.Writer.TryComplete();
        _logger.LogWarning("Removed stale event subscriber {SubscriberId}", id);
    }
}
```

Simpler alternative: track subscriber creation time and remove if buffer is full (indicating the consumer is not reading). This avoids a separate timer.

**Log subscriber cleanup failure (M13):**

Add warning log when `TryRemove` returns false in the finally block:

```csharp
if (!_eventSubscribers.TryRemove(subscriberId, out var channel))
{
    _logger.LogWarning("Failed to remove event subscriber {SubscriberId}", subscriberId);
}
```

---

## 8. Contract Test Completion (M14, M15, L3, L8, L9, L10)

### 8.1 Design

**Extend IWorkOrchestrator contract tests (M14):**

Add reflection-based signature tests for all 15 members. Missing members to add:
- `Run(TWork)` — void return, single parameter
- `TryRun(TWork)` — bool return, single parameter
- `CreateWorkerFunction()` — returns `Func<string, CancellationToken, Task>`
- `CreateWorkerFunction(Action<bool>?)` — overload with state callback
- `RequestScaleUpAsync(int, CancellationToken)` — Task return
- `RequestScaleDownAsync(int, CancellationToken)` — Task return
- `GetShutdownToken()` — returns `CancellationToken`

**Create IEventStreamOrchestrator contract tests (M15):**

New test class verifying:
- Extends `IWorkOrchestrator<T>`
- `GetEventStreamAsync<TEvent>` signature with `IOrchestratorEvent` constraint
- `EnqueueAsync(TWork, string?, CancellationToken)` overload exists
- `TryEnqueue(TWork, string?)` overload exists

**Add work item context to worker logs (L3):**

In `WorkOrchestrator.WorkerLoopAsync()`, include work item info in the error log (requires capturing the item before the try block):

```csharp
catch (Exception ex)
{
    _logger.LogError(ex, "Worker {WorkerId} failed to process work item: {WorkItem}", workerId, work);
}
```

**No breaking change detection tests (L9):** Add a test that verifies member count on core interfaces to catch accidental additions/removals.

**Decorator test improvements (L10):** Add an integration test that verifies a multi-decorator chain (event stream + autoscaling + base) processes work end-to-end without mocking the inner orchestrator.

---

## 9. Finding Cross-Reference

| Finding | Section | Change Type |
|---------|---------|-------------|
| H1 | 2 | Delete `AsChannelReader()`, use `CreateWorkerFunction()` |
| H2 | 2 | Implement coordinator control + events ports |
| H3 | 3 | Add `DroppedCount` + warning log to DLQ |
| H4 | 2 | Remove empty try block, use coordinator worker function |
| M1 | 4 | `Interlocked` for `_stopRequested` |
| M2 | 6 | Validate unique decorator orders |
| M3 | 5 | Delete duplicate `ScalingAction`, use Core version |
| M4 | 3 | Remove `IDeadLetterNotifier` interface |
| M5 | 2 | Delete fallback constructor |
| M6 | 3 | Throw on duplicate subscription |
| M7 | 6 | Add `Order` to handler decorators |
| M8 | 2 | Track pending scaling tasks |
| M9 | 4 | Document event handler contract |
| M10 | 7 | Remove stale subscribers when buffer full |
| M11 | 4 | Add timeout to `DisposeAsync` |
| M12 | 2 | Chain `ContinueWith()` for worker task faults |
| M13 | 7 | Log subscriber cleanup failure |
| M14 | 8 | Extend contract tests to all 15 members |
| M15 | 8 | Create `IEventStreamOrchestratorTests` |
| L1 | 2 | Simplify `Enabled` conditional |
| L2 | 5 | `init` accessors on `AutoscalingOptions` |
| L3 | 8 | Add work item context to error logs |
| L4 | 2 | Inherited via `CreateWorkerFunction()` |
| L5 | 5 | Document fallback policy behavior |
| L6 | 5 | Publish `WorkCompletedEvent` |
| L7 | 5 | FALSE POSITIVE — helper is used |
| L8 | 2 | Use `IOptions<AutoscalingOptions>` |
| L9 | 8 | Member count detection test |
| L10 | 8 | Multi-decorator integration test |

---

## 10. Files Modified

**Production code (~18 files):**
- `Bifrost/Autoscaling/AutoscalingCoordinator.cs` — implement 8 stubs
- `Bifrost/Autoscaling/AutoscalingEngine.cs` — track pending tasks, document event
- `Bifrost/Autoscaling/AutoscalingOptions.cs` — init accessors
- `Bifrost/Autoscaling/WorkerInfo.cs` — Interlocked for _stopRequested
- `Bifrost/Autoscaling/WorkerRegistry.cs` — ContinueWith for task faults
- `Bifrost/Autoscaling/ScalingAction.cs` — DELETE
- `Bifrost/Decorators/AutoscalingOrchestrator.cs` — rewrite RequestScaleUpAsync, delete AsChannelReader, remove fallback ctor
- `Bifrost/Decorators/EventStreamOrchestrator.cs` — stale subscriber cleanup, publish WorkCompletedEvent, log cleanup failure
- `Bifrost/DeadLetter/DeadLetterQueue.cs` — add DroppedCount + logger
- `Bifrost/DeadLetter/DeadLetterNotifier.cs` — throw on duplicate subscribe
- `Bifrost/DeadLetter/IDeadLetterNotifier.cs` — DELETE
- `Bifrost/DependencyInjection/WorkOrchestratorBuilder.cs` — validate order uniqueness, handler decorator ordering
- `Bifrost/DependencyInjection/AutoscalingExtensions.cs` — wire coordinator dependencies
- `Bifrost/WorkOrchestrator.cs` — DisposeAsync timeout, work item context in logs
- `Bifrost.Core/Events/ScalingEvent.cs` — explicit enum values
- `Bifrost.Resilience/ResiliencyPolicyGenerator.cs` — document fallback

**Test code (~5 files):**
- `Bifrost.Tests/Contracts/IWorkOrchestratorTests.cs` — extend to 15 members
- `Bifrost.Tests/Contracts/IEventStreamOrchestratorTests.cs` — new/extended
- `Bifrost.Tests/DependencyInjection/` — multi-decorator integration test
- Tests for all changed production code (TDD — tests written first)

---

## 11. Risk Assessment

| Risk | Mitigation |
|------|------------|
| Autoscaling coordinator wiring breaks DI | Existing DI tests + new integration test |
| Removing IDeadLetterNotifier breaks compilation | Internal interface, search all usages |
| init accessors break Options Pattern binding | Microsoft.Extensions.Options supports init setters |
| Removing fallback constructor breaks tests | Search test usage, update to use primary constructor |
| WorkCompletedEvent publishing adds overhead | Non-blocking TryWrite, same pattern as existing events |
