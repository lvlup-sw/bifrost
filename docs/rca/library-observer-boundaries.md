# Observer failure boundaries

Reviewed against the dependency-defect stack at `70dfa24` on 2026-11-11.

## Reproduction and causes

`ObserverBoundaryTests` failed all eleven cases before the changes:

- Two callback cases blocked both `Notify` and another subscriber until the
  first callback was released. Calling an async method runs its synchronous prefix
  inline; discarding its task does not move that prefix off the worker.
- Concurrent registration/disposal left an empty event reader open. Disposal could
  snapshot and clear the dictionary between the disposed check and registration.
  The stress reproduction uses 2,000 races; native scheduling is uncontrolled.
- Six cases (queue-wait and worker-state observers, three dispatch strategies)
  faulted completion without processing accepted items. The observation ran after
  dequeue and outside handler error isolation. The worker exited with work lost.
- Two rejection observation cases threw before returning the admission result or
  routing the rejected item into the DLQ.

## Fix and limits

Serialize event registration with disposal, keeping publishing outside that lock.
Queue each DLQ callback separately on the thread pool, retaining error isolation.
Catch and log failures from queue-wait, worker-state, and rejection observers so
observation cannot suppress processing or rejection routing.

Successful worker observations remain synchronous and allocate no new per-item
objects. Subscriber dispatch adds a task per callback on the failure path only;
no-subscriber notification returns before allocating a snapshot. Slow callbacks
can still exhaust shared thread-pool resources. Notifications remain best-effort,
concurrent, unbounded, and not drained at shutdown. Reliable delivery belongs to
an appropriate `IDeadLetterQueue` implementation, not these callbacks.

Existing event buffer eviction is intentional and covered by its own tests; this
change does not replace it with a new delivery contract. Never-enumerated streams
must still be owned by a live orchestrator and are released at its disposal.

## Validation

The eleven regression cases passed after the fixes. The main Release suite passed
854 tests, including DI composition, HTTP resilience, health, metrics, admission,
DLQ and hosted lifecycle coverage. Callback tests now await delivery explicitly.
Host: Fedora Linux 45, x64, .NET 10.0.11. Broader qualification is recorded in the
library investigation ledger; this result does not close issue #85.
