# Priority API migration

This is a breaking API revision. It removes the public `LockingPriorityQueue<TElement, TPriority>` and keeps exact priority dispatch inside Bifrost. Standalone exact-queue consumers should own a BCL `PriorityQueue<TElement, TPriority>` and synchronize **all** accesses, including count, inspection and mutation, with the same lock. The internal exact work binding uses this pattern with fixed `long` priorities. The benchmark project retains its own independent synchronized BCL baseline.

## MultiQueue construction and observation

```csharp
var queue = ConcurrentPriorityQueue<string, int>.Create(new()
{
    Capacity = 1024,       // null means unbounded, not an initial allocation size
    SubQueueCount = 32,    // null uses RoundUpToPowerOf2(4 * ProcessorCount)
    Stickiness = 1,
    BufferCapacity = 0,
});
```

The default and comparer-only constructors remain. Integer tuning constructors are no longer public. `Capacity` is nullable; `SubQueueCount`, `Stickiness` and `BufferCapacity` expose the actual settings. The default settings and MultiQueue routing algorithm are unchanged.

Replace `TryPeek` with `TryPeekBestObserved` and `TryDequeueMin` with `TryDequeueBestObserved`. These names describe their existing behavior: non-atomic scans skip unknown published tops and lock only a candidate stripe. Neither operation promises a global minimum under concurrent use. Both may return false while items remain. `TryDequeue` remains relaxed two-choice removal. Equal priorities have no FIFO stability guarantee.

`Count`, `IsEmpty`, `ToArray` and enumeration are observations across stripes. Copies are unordered and weakly consistent, unlike `ConcurrentQueue`'s moment-in-time enumeration. Creating an enumerator allocates the backing copy; its struct representation only avoids boxing during direct enumeration.

Arbitrary comparer exceptions now preserve the failing operation's resident contents, publication and capacity reservation. The guarantee excludes allocation failures and process-fatal failures. Comparers must remain consistent, support concurrent calls, and not reenter or mutate the queue. Stored priorities must not be mutated.

## Ordering and admission

Select `DispatchStrategy.Fifo`, `PriorityExact` or `PriorityRelaxed`. `PriorityBinding`, its `Auto` value and the `Priority` sentinel are removed. `QueueFullMode.Wait` is supported only with FIFO; `Reject` is supported by every strategy. Unsupported combinations throw at construction. FIFO defaults to Wait.

```csharp
services.AddWorkOrchestrator<MyWork>()
    .WithHandler<MyHandler>()
    .UsePriorityDispatch(DispatchStrategy.PriorityExact)
    .Build();
```

`UsePriorityDispatch` requires an explicit priority ordering and sets Reject. Producers can inject `IWorkWriter<TWork>` without receiving lifecycle or scaling controls. `Enqueue` returns an immediate `EnqueueResult`; `TryEnqueue` is its boolean convenience form; `EnqueueAsync` waits only when configured to do so. `Run` and `TryRun` are removed. Rejections distinguish capacity, watermark and shutdown, including through decorators. Cancellation throws and is not dead-lettered. The default `EnqueueResult` has `Status == Uninitialized`.

```csharp
var result = writer.Enqueue(work, new WorkEnqueueOptions(
    Class: WorkClass.Default,
    CorrelationId: "request-42"));
```

A null or omitted Class uses the configured classifier. Explicit Default now overrides it. A WorkClass can also convert directly to WorkEnqueueOptions. Correlation metadata uses the common options value rather than event-stream-specific enqueue overloads.

Priority boosts change a virtual-time ordering key. An older Batch key outranks a fresh Interactive key after the interactive boost **plus** the batch penalty. This does not bound actual waiting time: backlog, handler duration, worker availability and relaxed selection all matter. Equal timestamps do not imply FIFO stability.

## Configuration and lifecycle

DI registrations use named work and dead-letter options keyed by `WorkOptions.Name<TWork>()`. Read them through `IOptionsMonitor<TOptions>.Get(name)`. Autoscaling options, metrics, registries, coordinators and engines are keyed services using `typeof(TWork)`. Register `AutoscalingEngineHostedService<TWork>` and `AddAutoscalingHealthChecks<TWork>()` for each work type. Registrations for different work types no longer share these settings or state.

`Complete()` closes admission. `Completion` observes termination of all registered workers, including dynamically started workers. `DrainAsync(token)` closes admission and waits; cancellation cancels only the wait. `AbortAsync(token)` closes admission, signals worker cancellation, and waits for termination. It replaces the former cancellation-first `StopAsync`. The host integration drains until the host deadline, then signals abort. `DisposeAsync` aborts and awaits actual worker termination before releasing resources; a handler that ignores cancellation can therefore keep disposal pending. No timeout authorizes disposal of resources still in use.

Acceptance means admission to an in-memory queue, not durable delivery or successful execution. Abort, handler failure and process failure can prevent execution. Applications needing durable recovery must provide it separately.

## Conventions and research

These choices follow the BCL's [priority comparer and unstable tie semantics](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.priorityqueue-2), [channel completion and producer waiting](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels), [named options](https://learn.microsoft.com/en-us/dotnet/core/extensions/options#named-options-support-using-iconfigurenamedoptions), and [ConcurrentQueue snapshot enumeration](https://learn.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentqueue-1.getenumerator).

The [MultiQueue buffering paper](https://drops.dagstuhl.de/storage/00lipics/lipics-vol204-esa2021/LIPIcs.ESA.2021.81/LIPIcs.ESA.2021.81.pdf) describes the deletion-buffer resident-minimum invariant retained here. Stationary rank formulas from idealized deletion-only models are research context, not a hard contract for this implementation's distinct sampling, stickiness and fallback behavior. See the [review and validation record](https://github.com/lvlup-sw/bifrost/blob/4a958f59448e4a50ad6615f0d165fe62d43c9559/docs/benchmarks/2026-10-28-api-remediation.md) for applicability, measured overhead and the remaining server-performance release check.

The [performance follow-up](../benchmarks/2026-10-28-api-performance-followup.md) records the optimized comparer-safe implementation, repeated benchmarks and remaining workload-specific tradeoffs.

The [targeted performance investigation](../benchmarks/2026-10-28-api-targeted-performance.md) records the latest sparse-refill optimization, repeated dense four-thread measurements, and remaining overhead without changing MultiQueue's algorithm or comparer exception guarantee.

The [residual investigation](../benchmarks/2026-10-28-api-residual-performance.md) documents further rejected variants, the remaining workload-dependent costs, and expanded bounded/unbounded exception and duplicate-priority verification. It makes no further production change.
