# Scheduling lookup and persistence ordering

## Cron coalescing

`CronCadence` reuses the parsed Cronos 0.13.0 expression to find the previous occurrence at or before the cutoff. Coalesce accepts it only in `(lastFiredAt, cutoff]`. Null/startup behavior and the FireAllMissed cap remain unchanged. No new public cadence method is required.

`CronCoalesceTests` compares reverse lookup against forward enumeration across UTC, New York, Berlin, and Lord Howe calendar/DST boundaries, including leap dates, exact cutoffs, offsets, large backlogs, and date limits. Zero-jitter intervals use arithmetic. Custom/jittered cadences still enumerate. Reverse cron lookup avoids backlog enumeration without promising constant time for every calendar expression.

## Per-job gates and checkpoint order

`JobMutationGates` counts holders and waiters before awaiting. A short metadata lock protects entry admission and retirement; persistence runs outside that lock under the per-name semaphore. Cancellation releases its reference, and the last reference removes and disposes the entry. Unregister cannot retire a gate while an older waiter still references it. Idle names retain no entries.

Registry mutations and checkpoints share the gate. Independent names cannot collide; same-name storage effects remain serialized across replacements. This does not bound the existing scheduler backlog or support arbitrary reentrant store callbacks.

Checkpoint correctness does not depend on semaphore FIFO order. Under the gate, the registry checks job-generation identity and rejects timestamps at or before successful progress. It also records the highest attempted timestamp before calling the store and rejects older attempts after uncertain failures. The same uncertain timestamp can retry; newer occurrences can proceed. A failed-before-effect write can leave older durable progress and permit replay. Checkpoint-only record copies retain canonical generation identity; user mutations create a new generation.

`JobMutationGatesTests`, `CheckpointOrderingTests`, and the registry race/restart suites cover cancellation, retirement, churn, independent progress, replacement generations, and before/after-effect faults. Removing the checkpoint guard caused all three ordering cases to fail. The guard does not provide external-writer fencing or transactional rollback.

## Reproduce the benchmark

```sh
dotnet run --project tools/scheduling-overhead -c Release > artifacts/scheduling-overhead.json
```

Create `artifacts/` first if it does not exist. The harness compares the production gate against 64 stripes, alternates order, warms the synchronous path, and retains every repeat. Delayed cases use 64 actors with ten operations each and simulated 1/10 ms persistence. Collision names follow the current process's randomized hash.

The adoption criteria were at least 20% benefit under independent-name delayed persistence, less than 1 microsecond added uncontended cost, no material hot-name regression, and zero retained entries. Both process launches met those criteria on an i9-13900K, Fedora Linux 45, .NET 10.0.15, Release:

| Median measurement | Stripes | Per-job gates |
|---|---:|---:|
| Uncontended acquire/release, second launch | 29.82 ns; 0 B | 177.46 ns; 160 B |
| Independent names, 10 ms delay, first launch | 521.11 ms/batch | 105.76 ms/batch |
| Independent names, 10 ms delay, second launch | 413.90 ms/batch | 103.26 ms/batch |
| One hot name, 10 ms delay, second launch | 6627.29 ms/batch | 6629.49 ms/batch |
| Retained keyed entries after each batch | n/a | 0 |

A million-minute cron backlog took 22.50 ms with enumeration versus 1.36 microseconds with reverse lookup in the second launch. These bounded-concurrency microbenchmarks use simulated storage, short cron repetitions, and a non-isolated host. They demonstrate the algorithmic and collision costs, not production latency or database throughput. The synchronous store pays the added allocation cost. The registry barrier test establishes independent progress without a latency threshold.

Raw research and measurements were archived outside the repository from commit `79e622b`; regenerate results with the maintained harness. [Issue #86](https://github.com/lvlup-sw/bifrost/issues/86) tracks durable batching separately. [Issue #85](https://github.com/lvlup-sw/bifrost/issues/85) tracks remaining critical-path evidence gaps.
