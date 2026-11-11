# Scheduling overhead implementation

I adopted cron reverse lookup and exact-name mutation gates after comparing the prototype against PR #84 at `f008d76a2600b90d54ca2284e30d473bcb19039f`. The underlying persistence contract and jitter behavior remain unchanged. Batching is a separate spike: https://github.com/lvlup-sw/bifrost/issues/86.

## Cron

`CronCadence` reuses its parsed Cronos 0.13.0 expression to find the previous occurrence at or before the cutoff. Coalesce accepts it only in `(lastFiredAt, cutoff]`. Null/startup and future-last-fire cases return no backlog. FireAllMissed retains its cap. No new public cadence method or dependency is introduced.

`CronCoalesceTests` compares reverse lookup with forward enumeration for UTC, New York, Berlin, and Lord Howe, using dense, daily, month-end and leap-day expressions across DST and calendar boundaries. Additional checks cover exact ticks, nonzero offsets, one million missed minutes, date limits, and the unchanged FireAllMissed cap. The generic custom/jittered path still enumerates. Reverse cron search avoids backlog enumeration; it is not a universal O(1) calendar-search claim.

## Per-job gates

`JobMutationGates` leases an entry before waiting. The reference count includes holders and waiters. Admission and retirement share one short metadata lock; storage I/O runs outside that lock, inside the per-name semaphore. Cancellation releases its reference without releasing another caller's semaphore. The last reference removes and disposes the entry. Release wakes a waiter before dropping the holder's reference, so an entry cannot retire while any caller can still acquire or release it. Lease disposal is idempotent.

The registry uses the gate for mutations and checkpoints. Independent names no longer collide. Same-name persistence remains serialized, including unregister/re-register. There is no semaphore pooling or public configuration switch. Entry count is proportional to concurrently referenced names, not historical name count. This does not bound the existing scheduler command/checkpoint backlog or promise reentrant custom store callbacks.

Tests cover cancellation with another waiter, independent names, repeated disposal, retirement/reacquisition, 3,200 churn operations checking exclusive ownership, existing stale-generation/disposal regressions, and a real registry test holding one store call while a name that collided under the old stripes completes registration.

## Checkpoint ordering

Semaphore wake order is not a correctness assumption. Under the per-name gate, the registry checks generation identity and rejects timestamps at or before the successfully recorded occurrence. It also records the highest attempted timestamp before invoking the store and rejects older attempts after an uncertain failure. This prevents a write that succeeds then throws from being overwritten by an older checkpoint. The same uncertain timestamp can retry; later occurrences can proceed. A failed-before-effect write can leave older durable progress and permit replay. The guard does not acknowledge an occurrence whose write failed.

The attempted timestamp is scoped to the canonical job generation through a weak table. Checkpoint-only record copies retain identity; actual registry mutations create a new generation. This is not external-writer fencing or transactional rollback of an after-effect store failure. The existing mutation contract and its ambiguous failure limits remain.

New tests execute timestamps 3, 2, 3, stale generations after replacement, and before/after-effect exceptions followed by older, same, and newer writes. Removing the production guard makes all three cases fail assertions. The source was restored, rebuilt, and the full scheduling suite rerun. The queued-checkpoint test now awaits actual operations rather than assuming every obsolete checkpoint must reach the store.

## Adoption criteria and measurements

Before running, I required at least 20% benefit under independent-name delayed persistence, less than 1 microsecond added uncontended cost, no material hot-name regression, and zero retained entries after quiescence. I adopt the gates because both process launches met these criteria. Allocation increased by 160 bytes per uncontended acquire/release; this is an explicit cost, not a zero-allocation claim.

Environment: Fedora Linux 45, Linux 7.2.15-300.fc45.x86_64, Intel i9-13900K, 32 logical processors, .NET SDK 10.0.404/runtime 10.0.15, Release. The harness alternates implementation order, warms the synchronous path, and records five uncontended repeats plus three delayed repeats per scenario. Each delayed repeat runs 64 actors with ten operations each, holding the gate across an injected 1 or 10 ms delay. Collision names are selected against that process's randomized ordinal hash. Raw observations retain p50/p99 and live-entry counts.

| Measurement | Stripes | Per-job gates |
|---|---:|---:|
| Uncontended operation, first process median | 32.31 ns, 0 B | 180.14 ns, 160 B |
| Uncontended operation, second process median | 29.82 ns, 0 B | 177.46 ns, 160 B |
| Independent names, 10 ms store, first median batch | 521.11 ms | 105.76 ms |
| Independent names, 10 ms store, second median batch | 413.90 ms | 103.26 ms |
| One hot name, 10 ms store, second median batch | 6627.29 ms | 6629.49 ms |
| Deliberate collisions, 10 ms store, second median batch | 6596.27 ms | 103.90 ms |
| Retained keyed entries after each batch | n/a | 0 |

The first process linked the prototype source; the second references the adopted production assembly. Both use the same gate implementation. These are bounded-concurrency microbenchmarks, not externally paced production latency measurements. Store delay is simulated, no durability or database throughput claim follows, and the host was not isolated. Small timing differences are not meaningful. The included synchronous store pays the added allocation/bookkeeping cost; delayed independent jobs avoid false serialization. The registry-level barrier test establishes the benefit without timing assumptions.

The second process also checks cron answers and compares ten calls per repeat (three repeats). Median time per call for a million-minute backlog was 22.50 ms forward versus 1.36 microseconds reverse; at 100 missed minutes it was 16.21 microseconds versus 0.44 microseconds. These short runs demonstrate the algorithmic difference, not a stable sub-microsecond service-latency estimate.

Run:

```sh
dotnet run --project tools/scheduling-overhead -c Release > overhead.json
dotnet run --project src/Bifrost.Tests.Scheduling -c Release -- --minimum-expected-tests 359
python3 scripts/run-invariant-tests.py
```

Raw measurements, negative-control output, and validation are in `overhead-evidence/`. The new test classes are included in the invariant CI lane. Research rationale: https://github.com/HangfireIO/Cronos/blob/v0.13.0/src/Cronos/CronExpression.cs and https://learn.microsoft.com/en-us/dotnet/api/system.threading.semaphoreslim .
