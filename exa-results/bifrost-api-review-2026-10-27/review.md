> Historical review of baseline `6800ae0`. Its API proposals are superseded by the breaking migration and [implementation/validation record](../../docs/benchmarks/2026-10-28-api-remediation.md). Code links describe the reviewed revision, not current line locations.

# Bifrost API review and improvement proposal

The highest-value change is to make ordering, failure, progress, and lifecycle guarantees precise. Keep both the scalable MultiQueue and the exact locking queue, but make their different contracts obvious before callers choose one. Fix bounded-capacity recovery and per-work-type configuration isolation before adding convenience methods.

This review covers the concurrency package in depth and the orchestrator's public interfaces, admission, configuration, and shutdown integration. Scheduling abstractions received a surface review; this is not a correctness audit of every integration. The implementation reviewed matches checkout `3a17ed0` on October 27, 2026. The README and background documentation were independently revised during the review; those revisions were checked, and findings below refer to the remaining issues.

## Findings that affect correctness

### 1 Clarify the meaning of strict minimum and failed attempts

**P1 — contract mismatch.** `TryDequeueMin` is advertised as the strict alternative when relaxed rank error is unacceptable. Its scan skips unreadable sub-queues, however, and neither the winner scan nor the runner-up scan observes the entire queue at one instant. Holding the winning stripe through removal fixes a local race; it does not establish a global minimum. See [the strict claim](/Users/rsalus/Dev/aima/bifrost/src/Bifrost.Concurrency/ConcurrentPriorityQueue.Inspect.cs:164) and [the skipped unknown tops](/Users/rsalus/Dev/aima/bifrost/src/Bifrost.Concurrency/ConcurrentPriorityQueue.Inspect.cs:314).

A deterministic test-hook schedule leaves priority 1 in an unreadable stripe and priority 10 in another. `TryDequeueMin` succeeds with 10 while 1 remains. This simulates a writer paused during publication. A separate schedule holds the only populated stripe's lock: both `TryPeek` and `TryDequeueMin` return false with `Count == 1`. Their remarks partially acknowledge failure on contention, but surrounding promises of exact ordering contradict that behavior.

.NET's [PriorityQueue](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.priorityqueue-2?view=net-10.0) removes the minimum, while [ConcurrentQueue.TryDequeue](https://learn.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentqueue-1.trydequeue?view=net-10.0) describes atomic removal and internally resolves competition between consumers. The [Try-pattern guidelines](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/exceptions-and-performance) require a precisely defined failure case; the prefix alone does not mean lock-free or exception-free.

**Proposal:** retain the existing implementation as explicitly best-observed inspection, with names such as `TryDequeueBestObserved` and `TryPeekBestObserved`. Document that false means no result was obtained, including contention. Direct callers needing exact ordering to `LockingPriorityQueue`. If exact operations must exist on the MultiQueue itself, implement a coordinated global observation/removal mechanism and publish its blocking cost. Do not describe the existing method as exact under concurrent mutation.

Also narrow `TryDequeue`'s [empty-result wording](/Users/rsalus/Dev/aima/bifrost/src/Bifrost.Concurrency/ConcurrentPriorityQueue.Dequeue.cs:110): observing each stripe empty during one pass does not establish that the whole queue was empty at one instant. A producer can fill an already-scanned stripe before another consumer empties a later stripe. State the actual scan guarantee, or add a proven global validation protocol if atomic emptiness is required. Consumers should use explicit completion protocols for termination.

### 2 Repair capacity accounting after comparer exceptions

**P1 — reproduced implementation defect.** [HeapPush](/Users/rsalus/Dev/aima/bifrost/src/Bifrost.Concurrency/SubQueue.cs:423) increments `_size` before a comparison can throw. The [enqueue catch](/Users/rsalus/Dev/aima/bifrost/src/Bifrost.Concurrency/ConcurrentPriorityQueue.Enqueue.cs:156) then releases the capacity reservation as if no storage mutation occurred. [Clear](/Users/rsalus/Dev/aima/bifrost/src/Bifrost.Concurrency/ConcurrentPriorityQueue.Collection.cs:117) subtracts the stripe's actual size, driving the reservation count negative.

The focused reproduction produced:

```text
After comparer failure: Count=1, stored entries=2, reservations=1
After Clear: reservations=-1
After Clear and refill: accepted=3, Count=3, BoundedCapacity=2
```

The existing [rollback test](/Users/rsalus/Dev/aima/bifrost/src/Bifrost.Tests.Concurrency/BoundedCapacityEdgeTests.cs:145) explicitly allows heap corruption and checks only the reservation counter. That misses the later violation of the advertised hard capacity.

**Proposal:** define exception safety explicitly and make storage, published state, and reservations agree after failures. At minimum, guarantee that `Clear()` safely restores an empty, reusable queue after a comparer failure, or mark the instance faulted and reject subsequent operations until a coordinated reset. Audit buffered insertion and removal too. Do not fix this by merely clamping a negative counter: reservations can belong to concurrent producers. .NET's [exception guidance](https://learn.microsoft.com/en-us/dotnet/standard/exceptions/best-practices-for-exceptions) supports preserving recoverable state; it does not establish a blanket transactional guarantee for BCL priority queues.

Document that priority values must remain order-stable while queued and that custom comparers may be invoked concurrently across stripes. Prefer deterministic, thread-safe, nonthrowing comparers. `Try` methods may still propagate comparer exceptions.

### 3 Isolate configuration for each work type

**P1 — configuration defect established by source.** `AddWorkOrchestrator<A>` and `AddWorkOrchestrator<B>` both configure the same unnamed `WorkOrchestratorOptions`. Each builder resolves that same options instance. A later capacity assignment affects both orchestrators; enabling priority dispatch for one can enable it for the other. See [registration](/Users/rsalus/Dev/aima/bifrost/src/Bifrost/DependencyInjection/ServiceCollectionExtensions.cs:39), [resolution](/Users/rsalus/Dev/aima/bifrost/src/Bifrost/DependencyInjection/WorkOrchestratorBuilder.cs:148), and [priority configuration](/Users/rsalus/Dev/aima/bifrost/src/Bifrost/DependencyInjection/PriorityDispatchExtensions.cs:69).

**Proposal:** give each work-type registration named options, or use a generic options type keyed by `TWork`. A singleton can resolve a named value through `IOptionsMonitor<T>.Get(name)` and capture the validated configuration at construction. Carry the same key through all builder extensions; review autoscaling and dead-letter settings for equivalent sharing. Microsoft explicitly provides [named options](https://learn.microsoft.com/en-us/dotnet/core/extensions/options#named-options-support-using-iconfigurenamedoptions) for independent configurations of the same shape. Add a two-work-type test with distinct capacities and dispatch strategies.

### 4 Remove unsupported starvation and progress promises

**P1 — misleading guarantees.** [PriorityDispatchOptions](/Users/rsalus/Dev/aima/bifrost/src/Bifrost.Core/PriorityDispatchOptions.cs:24) and [PriorityKey](/Users/rsalus/Dev/aima/bifrost/src/Bifrost/Queues/PriorityKey.cs:23) still equate the boost window with a starvation bound. A 30-second priority advantage cannot bound queue waiting while workers are busy, backlog remains, or relaxed deletion repeatedly overlooks an item.

Even the key-order comparison needs a qualification. For a Batch item enqueued at time `t`, penalty `P`, and Interactive boost `B`, its key is `t + P`; a fresh Interactive item's key is `now - B`. Batch outranks that fresh arrival only when `now - t > B + P`, not merely `B`. Exact dispatch then honors that key ordering; it still promises no wall-clock deadline.

**Proposal:** call these bounded priority offsets. State separately what ordering they induce and whether the chosen queue enforces it exactly. If bounded service delay is a product requirement, specify service-time, arrival-rate, and scheduling assumptions and implement an appropriate fairness mechanism. [Engineering MultiQueues](https://drops.dagstuhl.de/storage/00lipics/lipics-vol204-esa2021/LIPIcs.ESA.2021.81/LIPIcs.ESA.2021.81.pdf) distinguishes rank error from per-element delay precisely because low average rank error does not prevent an individual element being postponed.

Likewise, replace unconditional lock-free/wait-free language with the actual progress contract. [Enqueue retries without a bound](/Users/rsalus/Dev/aima/bifrost/src/Bifrost.Concurrency/ConcurrentPriorityQueue.Enqueue.cs:140), and [dequeue verification retries contended stripes](/Users/rsalus/Dev/aima/bifrost/src/Bifrost.Concurrency/ConcurrentPriorityQueue.Dequeue.cs:334). If a thread holds the sole nonempty stripe, dequeue can depend on that thread resuming. Processor count does not cap the number of managed threads holding locks. The research's probabilistic progress argument assumes a relationship between queues and participating threads and uses a qualified notion of wait-freedom; it is not an unconditional latency guarantee for this API.

### 5 Separate theoretical quality estimates from API guarantees

**P2 — overclaimed research applicability.** The [public dequeue remarks](/Users/rsalus/Dev/aima/bifrost/src/Bifrost.Concurrency/ConcurrentPriorityQueue.Dequeue.cs:116) and [background](/Users/rsalus/Dev/aima/bifrost/src/Bifrost.Concurrency/BACKGROUND.md:34) attach the stationary formula `(5/6)n - 1 + 1/(6n)` to the implementation. [Walzer and Williams](https://arxiv.org/html/2410.08714) derive it for an idealized deletion-only process with a particular sampling distribution. Their discussion leaves arbitrary interleaved insertion and stickiness analysis open.

This implementation uses [two distinct samples](/Users/rsalus/Dev/aima/bifrost/src/Bifrost.Concurrency/ThreadHandle.cs:176), bounded sampling followed by occupancy routing, and optional stickiness. The paper's displayed probability `1 - (1 - i/n)^2` corresponds to independent sampling with replacement. Thus its exact coefficient is not an established theorem for this implementation. The background's statement that no exact tail distribution has been published also needs updating: the paper characterizes the long-term distribution in its model.

**Proposal:** keep the formula in background material with its assumptions. Public documentation should promise relaxed selection, explicitly exclude a deterministic rank or delay bound, and link to measured quality distributions for supported configurations. Report mean, p95/p99, observed maximum, and element delay separately. A finite queue naturally limits rank error by its current population; that is different from a useful fixed quality guarantee.

### 6 Make snapshot consistency explicit

**P2 — incorrect .NET precedent.** [Collection documentation](/Users/rsalus/Dev/aima/bifrost/src/Bifrost.Concurrency/ConcurrentPriorityQueue.Collection.cs:23) calls the striped copy a weak snapshot, correctly, but attributes that guarantee to `ConcurrentQueue`. Microsoft documents a [moment-in-time snapshot for ConcurrentQueue enumeration](https://learn.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentqueue-1.getenumerator?view=net-10.0). [ConcurrentDictionary enumeration](https://learn.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentdictionary-2.getenumerator?view=net-10.0) has weaker consistency. These are different precedents.

**Proposal:** describe `ToArray` as an unordered, non-atomic copy collected across stripes. The returned array is fixed, but its capture does not represent one instant. Make the same distinction for `Count` and `IsEmpty`; they are observations, not coordination primitives. Explain that enumeration allocates and copies O(items), even though the enumerator itself is a struct. Retain current APIs for compatibility. Add an explicitly named atomic snapshot only if callers need it and are willing to pay for coordinating all stripes.

### 7 Align admission and shutdown contracts with behavior

**P1 for shutdown, P2 for result precision.** The [StopAsync interface summary](/Users/rsalus/Dev/aima/bifrost/src/Bifrost.Core/IWorkOrchestrator.cs:116) promises pending work completion, but [the implementation](/Users/rsalus/Dev/aima/bifrost/src/Bifrost/WorkOrchestrator.cs:390) cancels workers before completing admission. The hosted service repeats the graceful-drain claim. `DrainAsync` is the separate operation that waits for natural consumption. The interface also promises at-least-once execution for admitted work, which an in-memory queue cannot guarantee across immediate stop or process failure.

**Proposal:** publish a lifecycle table covering admission closure, queued work, running handlers, cancellation, and completion. Preserve existing behavior in a patch and correct its documentation. For a future API, consider distinct `Complete`/`Completion`, `DrainAsync`, and `AbortAsync` concepts, or explicit stop modes. Decide whether canceling a drain cancels only the caller's wait or also the work. [Channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels) provides useful precedents for separating producer completion, consumption, and asynchronous waiting.

For admission, [EnqueueAsync](/Users/rsalus/Dev/aima/bifrost/src/Bifrost/WorkOrchestrator.cs:316) maps every priority failure to `CapacityExceeded`, despite exposing `WatermarkExceeded` in [RejectionReason](/Users/rsalus/Dev/aima/bifrost/src/Bifrost.Core/RejectionReason.cs:16). Propagate the decision from the admission check itself; do not infer it from a later count read. Add an outcome-bearing synchronous admission operation, while retaining the boolean convenience method.

Keep FIFO waiting and priority rejection as deliberate policies, but make the choice explicit at registration and observable on the resolved configuration. Changing dispatch ordering currently changes backpressure too. Do not silently add a producer wait queue without deciding how waiting producers are prioritized.

## API improvements after the contract fixes

| Area | Current issue | Proposed direction |
|---|---|---|
| Construction | An integer means a hard bound for `ConcurrentPriorityQueue`, but initial allocation for `LockingPriorityQueue` and BCL `PriorityQueue`. `-1` is rejected by the simple bounded constructor but accepted by tuning overloads. | Preserve existing meanings. Add an options-based factory that separates `BoundedCapacity` from `InitialCapacity`, with one representation of unbounded capacity in the new API. |
| Tuning | Several adjacent integer parameters; the public docs recommend pinning stripe count through an internal constructor. | Add validated options for sub-queue count, stickiness, and buffering. Expose resolved values read-only. Copy options at construction; do not make tuning mutable on a live queue. |
| Ordering intent | `Auto` always chooses MultiQueue; `Binding` exposes implementation choices. | Add explicit exact/relaxed intent in a future configuration surface. Keep existing `Auto` behavior stable and document it as a default alias. Avoid an automatic policy that silently changes ordering guarantees. |
| Exact queue parity | `LockingPriorityQueue` lacks `Comparer`, `IsEmpty`, and `ToArray`, although it is a supported production alternative. | Add these useful inspection members with their locking costs. Add bounded admission only with a clearly separate maximum-capacity contract. |
| Enumeration | Direct enumeration can suggest priority order; a value-type enumerator can suggest allocation-free traversal. | Keep unordered enumeration and document its cost. Consider an `UnorderedItems` view for discoverability, without duplicating snapshots or changing old enumeration behavior. |
| Equal priorities | No simple public tie-order contract. | State that FIFO among equal priorities is not guaranteed, matching BCL `PriorityQueue`. A composite priority with an application sequence can define a tie order for the exact queue; it does not make relaxed dequeue stable. |
| Producer interface | `IWorkOrchestrator` includes producer methods, worker-loop factories, scaling, lifetime, and a shutdown token. | Introduce a small producer-facing interface. Keep worker/lifecycle controls on separate capabilities; retain the existing interface as a compatibility facade. |
| Method names | `Run` only enqueues; `TryRun` aliases `TryEnqueue`. | Make enqueue terminology canonical in examples and future APIs. Phase out aliases through a documented deprecation policy. |
| Work class override | `WorkClass.Default` means defer to classifier, so callers cannot explicitly override a classifier back to Default. | Use a distinct unspecified value in a new overload/options object, such as nullable `WorkClass`, while preserving old overload semantics. |
| Admission result | `default(EnqueueResult)` is rejected with no reason, contrary to the property's stated invariant. | Define an explicit uninitialized status or a documented valid default. Preserve a small immutable value type; .NET recommends [valid zero-initialized structs](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/struct). |

An additive construction sketch, not an implemented API:

```csharp
var queue = ConcurrentPriorityQueue<Job, long>.Create(
    new ConcurrentPriorityQueueOptions
    {
        BoundedCapacity = 1_024, // null means unbounded in the new options API
        SubQueueCount = 32,     // explicit advanced choice; otherwise auto-sized
        Stickiness = 1,
        BufferCapacity = 0,
    },
    comparer: Comparer<long>.Default);
```

A named factory avoids making existing `new ConcurrentPriorityQueue<T, P>(null)` calls ambiguous between a comparer and a new options reference type. Constructor overloads should remain usable and unchanged. .NET's [compatibility rules](https://learn.microsoft.com/en-us/dotnet/core/compatibility/library-change-rules) are a useful checklist for overload resolution, parameter names, interface additions, and behavior changes. Even adding an obsolete warning deserves release planning for consumers treating warnings as errors.

Do not add the entire BCL priority-queue surface for symmetry. Compound operations such as `EnqueueDequeue`, arbitrary removal, and reprioritization need an explicit atomicity contract. Bulk enqueue is useful only after defining partial success and capacity handling. `IProducerConsumerCollection<(TElement, TPriority)>` could enable [BlockingCollection integration](https://learn.microsoft.com/en-us/dotnet/standard/collections/thread-safe/blockingcollection-overview), but should be a tested adapter with exclusive ownership and compatible add/take semantics. Wrapping the current best-observed methods directly would make failure-on-contention problematic. Async waiting belongs in a separate adapter, as the existing work-queue layer already does.

Preserve the good existing choices: separate element and priority types, default/custom comparers, `[MaybeNullWhen(false)]` outputs, immutable admission values, cancellation distinct from rejection, and the separation of queue storage from orchestration. Scheduling's cancellation-last signatures and `TimeProvider` references are consistent with the broader API direction; they do not need redesign merely for naming symmetry.

## Suggested delivery order and verification

1. **Patch:** fix exception recovery/accounting and configuration isolation. Correct strict-minimum, progress, snapshot, shutdown, and starvation documentation. Complete admission reason propagation. Keep existing constructor meanings and dispatch defaults.
2. **Additive release:** introduce options-based construction, resolved tuning diagnostics, best-observed operation names, exact-queue inspection parity, and a producer-only interface. Plan deprecations explicitly.
3. **Breaking release only if justified:** simplify aliases and sentinel values, make ordering and full-queue policy explicit, and consolidate lifecycle operations. A class rename to include `Relaxed` is optional; precise documentation and explicit construction may suffice.

Validation should target contracts, not just throughput: deterministic paused-writer schedules; comparer failures at each mutation stage followed by clear/refill; two work types with independent options; distinct watermark/full/shutdown results; and stop/drain/cancellation histories. Test buffered and unbuffered paths. Use a history checker for any promised linearizable operations. Keep stress tests for conservation and contention, and measure ordering quality separately from throughput.

The existing regular concurrency suite passed **127/127 tests** on .NET SDK 10.0.101, macOS arm64. Stress-category tests, full-solution tests, AOT publishing, and performance benchmarks were not run. The focused executable compiled the unchanged queue source and reproduced the three cases described above. Its [source](/Users/rsalus/Dev/aima/bifrost/exa-results/bifrost-api-review-2026-10-27/repro/Program.cs) and [project](/Users/rsalus/Dev/aima/bifrost/exa-results/bifrost-api-review-2026-10-27/repro/Review.csproj) are included for repeatability. Run it from the repository root with:

```sh
mkdir -p /tmp/bifrost-baseline
git archive 6800ae0 src/Bifrost.Concurrency | tar -x -C /tmp/bifrost-baseline
dotnet run --project exa-results/bifrost-api-review-2026-10-27/repro/Review.csproj -c Release \
  -p:QueueSources=/tmp/bifrost-baseline/src/Bifrost.Concurrency
```

Exa research covered 25 search-result entries across three workstreams: .NET collections, .NET library conventions, and MultiQueue research, followed by targeted full-page reads. Duplicate framework-version pages and duplicate paper mirrors were consolidated. Recommendations above distinguish Microsoft contracts, research assumptions, observed behavior, and proposed design choices. Older Framework Design Guidelines pages are used selectively alongside .NET 10 APIs; obsolete statements about C# language capabilities are not adopted.
