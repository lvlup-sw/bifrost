# Q5 observed-empty histories

`ObservedEmptyInvariantTests` covers a bounded set of genuinely concurrent executions of `TryDequeue`. It checks the documented full authoritative empty-pass contract without requiring global priority ordering or a linearizable concurrent snapshot. No production hooks were added.

## Permitted histories and independent evidence

- **Resident prefill:** 49 completed enqueues precede 48 total removal attempts. Four dedicated actors run 16 barrier-started rounds: one producer and three consumers. At least one original entry survives every attempt, even if every attempted removal succeeds and all producers are delayed. Original entries do not migrate between stripes, so a full pass cannot honestly see every stripe empty. Every dequeue must succeed. The oracle never uses concurrent `Count` or `ToArray` to infer emptiness. Unique IDs and priorities are checked against the independent set of accepted entries; only after all actors finish is the remaining snapshot checked.
- **Held sparse last item:** the existing internal push and `SyncLock` put one item in the final stripe and hold that real lock. Public `TryDequeue` reaches the existing scan-entry counter while the lock is held. After release it must return that exact item, rather than report empty. A subsequent quiescent false must record all stripes observed empty. Direct internal insertion deliberately bypasses occupancy hints; it uses an unbounded queue and the real published subqueue state.
- **Overlapping producer:** a lawful ascending integer comparer blocks only the producer thread during public `Enqueue(2, 2)` on a one-stripe heap already containing `(1, 1)`. The comparer always returns the normal comparison result after release. Public `TryDequeue` starts while that enqueue is blocked and reaches the authoritative scan. The recorded producer interval must contain the consumer invocation. The resident original item makes false impermissible regardless of when the second entry becomes visible. After release, one matching removal and one matching quiescent remainder must conserve both IDs.
- **Competing last-item boundary:** two dedicated consumers race for one item on stripe 127. Exactly one succeeds with the original ID and priority; exactly one returns false. The false may finish before the successful call returns, but cannot finish before that call starts. Its final empty pass must contain 128 observations. No choice of winning actor or cross-stripe minimum is prescribed.

The history records monotonically numbered invocation/completion boundaries, actor, actor step, operation, result, ID, and priority. Assertion failures print those records in invocation order. Per-actor order is checked in the generated prefill histories. These are call-boundary observations, not linearization points or exact OS schedule replays. Barriers launch the generated actors together but do not force every pair of calls to overlap; the lawful-comparer fixture explicitly establishes actual overlap.

## Domain and limits

Default generated prefill coverage is 36 histories: stripe counts 1, 4, 128; buffer capacities 0, 1, 16; four seeds; stickiness 4; unbounded capacity; integer payload IDs and three equal-priority classes. Each history has 16 concurrent enqueues and 48 removal attempts after prefill. The seed controls actor spin variation, not OS scheduling. `BIFROST_INVARIANT_SEED_MULTIPLIER` expands the seed count. The sparse held-lock fixture covers stripe counts 1, 4, 128. The comparer fixture uses one stripe and buffer capacity 0. The competing-last-item fixture runs 16 seeds and expands with the same multiplier.

This does not enumerate all interleavings, memory reorderings, comparer implementations, capacities, reference payloads, or workloads whose items are continuously inserted and removed without a resident survivor. It does not establish exact global minimum removal, FIFO order of equal priorities, or snapshot linearizability. Snapshot and `Count` assertions run only at quiescence. Existing scan instrumentation supports path evidence; the behavioral no-false oracle does not derive its conclusion from those counters. Builds with test hooks stripped cannot run the controlled scan-entry fixture unchanged.

The 100 ms hold window gives a broken false path a chance to complete before release. The assertion does not equate absence of completion with correctness: after release it inspects the actual result and identities. Five/ten/thirty-second bounds are harness watchdogs. A timeout alone is not counted as a mutation kill.

## Validation and actual production mutations

Executed locally against base `247ddb6`, .NET SDK 10.0.303 / runtime 10.0.11, Release. Build succeeded with only the existing local-remote SourceLink warnings. Final restored scoped run passed **4/4**; full concurrency run passed **160/160**.

```sh
dotnet build src/Bifrost.Tests.Concurrency/Bifrost.Tests.Concurrency.csproj -c Release
dotnet src/Bifrost.Tests.Concurrency/bin/Release/net10.0/Bifrost.Tests.Concurrency.dll --treenode-filter '/*/*/ObservedEmptyInvariantTests/*' --minimum-expected-tests 4 --no-ansi
dotnet src/Bifrost.Tests.Concurrency/bin/Release/net10.0/Bifrost.Tests.Concurrency.dll --minimum-expected-tests 160 --no-ansi
```

Two temporary edits were applied separately to the actual production file `src/Bifrost.Concurrency/ConcurrentPriorityQueue.Dequeue.cs`, built, and tested with the scoped command. Production source was restored in a `finally` block, rebuilt, and retested afterward. The final change contains no production edit.

| Production mutation | Actual scoped result | Behavioral witness |
| --- | --- | --- |
| Replace calls to `TryDequeueVerificationScan(out element, out priority)` after routing with default outputs and `return false` | Exit 2; 4 failed, 0 passed | Held last-item and blocked producer calls returned false; generated prefill also returned false with a guaranteed survivor. Last-item test rejected missing full-pass evidence. |
| Replace scan's `passVoided = true; break;` on contention with `emptyObserved++; continue;` | Exit 2; 3 failed, 1 passed | Held last-item and blocked producer calls returned false; generated prefill returned false despite the survivor bound. |

Both mutations compiled successfully. Neither kill was a build failure, timeout, or a deliberately failing test assertion disconnected from the queue. The lawful-comparer failure histories in both runs were:

```text
actor=0 step=0 enqueue start=1 end=4 success=true id=2 priority=2
actor=1 step=0 dequeue start=2 end=3 success=false id=0 priority=0
```

The resident prefill item `(1, 1)` had no other remover. The false therefore has no permitted empty observation in that actual overlapping call interval. The held-last-item fixture likewise recorded a completed false result while the sole entry had no other remover. The competing-last-item test passed the contention mutant, correctly permitting its one-success/one-empty outcome; the controlled tests supply the missing contended-nonempty witness.
