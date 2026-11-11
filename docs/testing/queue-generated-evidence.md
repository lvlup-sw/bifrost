# Generated queue invariant evidence

Base: `0fc121384950d964722a8de36d72a26679a428d3`. Scope: `ConcurrentPriorityQueue`, tested through its public methods with TUnit. No production changes are retained. No Exarchos tools were used.

## Contracts and domains

| Family | Domain and boundary | Oracle and observation boundary | Executed cases |
| --- | --- | --- | --- |
| Q1 | Seeds 0–31; 300 offered operations; bounded capacity 33; four stripes; buffers 0, 1, 16; unique item IDs, three duplicate payload values, seven tied priority values | Independent dictionary of successful enqueue effects, deleted only by successful removals or quiescent Clear. Every quiescent snapshot has the same multiset. Accepted = removed + cleared + resident. False removal implies empty only without concurrent actors. | 96 histories; 28,800 operations |
| Q2 | Capacities 1, 2, 3, 4, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65; stripes 1, 4; buffers 0, 1, 16 | Fill exactly to bound, reject without snapshot changes, remove one and refill one, Clear, then refill the whole capacity again. | 96 configurations; 288 cycles |
| Q3 | Seeds 0–15; 80 entries; ascending and descending lawful custom comparers; integer and reference-record priorities; MinValue, MaxValue, -1, 0, 1; buffers 0, 1, 16; four stripes | At quiescence, best-observed peek/removal equals a linear reduction over the effect ledger. Remove the actual identity returned; never require stable tie order. | 192 histories; 15,360 insertions and removals |
| Q4 | Seeds 0–3; prefixes 2, 3, 16, 17, 33, 65; single stripe; buffers 0, 1, 16; next operation enqueue or dequeue; bounded capacity 96 | Rebuild each prefix, verify before snapshot, inject at comparison positions until the first unreached position. Every reached throw preserves the ledger snapshot. Disable faults, drain to independent minima, refill all 96 slots, reject the next item, Clear and enqueue again. | 144 prefix/operation configurations; 884 attempts; 740 reached throws; 144 termination probes |
| Q5 controlled | Six actor permutations, three buffers, three actors, twelve rounds, capacity 3, two stripes; 36 operations per history | Semaphore gates select one whole operation at a time in the explicit actor order. Admission, actual removal identity, priority, and snapshot are checked after every completed operation. False returns are checked only at this nonoverlapping boundary. | 18 explicit schedules; 648 operations |
| Q5 native | Seeds 0–15; three buffers; three actors; 40 start/end barrier phases; capacity 17; four stripes | Record successful admissions and removals separately; after all actors finish, subtract actual removals, reject duplicate/invented identities, compare final snapshot and drain. No concurrent false return is interpreted as an exact global emptiness claim. | 48 barrier histories; 5,760 offered operations |
| Metamorphic | Seeds 0–31; 96 entries; priorities -1000 through 1000; stripes 1, 2, 4; buffers 0, 1, 16 | Quiescent drain priorities are nondecreasing. Canonicalized identity/priority multisets compare configurations, +1234 priority translation (no overflow), payload bijection and its inverse, exact multiplicity doubling, and Clear-prefix/fresh-suffix equivalence. Canonicalization removes tie-order assumptions. | 32 baselines plus 1,440 follow-up drains; 288 evaluations of each relation |

The ledger is an effect multiset, not a heap implementation or clone of internal queue state. Q1/Q2/Q3/Q4 distinguish unique operation identities from duplicate values; the metamorphic doubling case also enqueues exactly equal items twice.

Native barrier seeds regenerate offered actions but do not reproduce OS scheduling, stripe routing randomness, or linearization order. The controlled actor permutations reproduce operation-level schedules, not internal instruction interleavings. No concurrent Clear or globally atomic snapshot claim is tested.

## Commands and results

```sh
dotnet run --project src/Bifrost.Tests.Concurrency -- --treenode-filter '/*/*/GeneratedQueueInvariantTests/*' --output Detailed --no-ansi
python3 docs/testing/queue-mutation-check.py
```

The baseline passed all seven test methods in 280 ms of test runtime on .NET 10.0.11 / TUnit 1.2.11 / Linux x64. Build time is separate. The entire concurrency project then passed 150/150 tests in 2.744 seconds using `dotnet run --no-build --project src/Bifrost.Tests.Concurrency -- --no-ansi`. `queue-baseline.log` records the detailed output. The only build warnings concern the local filesystem Git remote and missing SourceLink URL.

The mutation script temporarily edits the actual production implementation, executes the selected generated test, records output and exit status, and restores the original source in `finally`. Run it only in an isolated worktree with no concurrent builds. It does not mutate the reference ledger and does not count compilation failures as killed mutants. `queue-mutation-results.json` records wall times including builds; the corresponding logs show assertion failures.

| Family | Temporary production mutation | Detection |
| --- | --- | --- |
| Q1 | Skip `LockedClear` | Seed 0, buffer 0, step 4: count mismatch |
| Q2 | Capacity rejection `>=` becomes `>` | Capacity 1: excess enqueue accepted |
| Q3 | Reverse winner selection in published minimum scan | Seed 0, buffer 0, remaining 79: peek is not minimum |
| Q4 | Omit bounded reservation decrement on comparer failure | Seed 0, buffer 0, prefix 2, enqueue comparison 1: recovery cannot refill capacity |
| Q5 | Push the accepted item twice before returning success | Controlled schedule detects resident count mismatch |
| Metamorphic | Skip `LockedClear` | Clear-prefix suffix drain has extra entries |

All six mutants fail against the real implementation. The metamorphic mutation demonstrates Clear-prefix sensitivity; it does not claim separate mutation adequacy for each of the other four relations. No production defect was found by these histories. This finite generated corpus and six mutation operators are evidence, not exhaustive correctness or linearizability proofs.

## Replay integration

Failure messages carry family, seed/configuration, step or fault position, or explicit actor permutation/round/actor. Seeds regenerate the source actions on this runtime; they are not persisted semantic replay documents. Shared `HistoryCase`/`ReplayRecord` integration remains a separate integration task. Preserve the source history and derived follow-up together when adding the shared replay adapter. For Q4 preserve fault position, prefix and operation; for Q5 preserve explicit schedules separately from the native barrier seed.

The file disables only documentation diagnostics (`CS1591`, `SA1600`) to honor the request not to add code comments. Other analyzers remain enabled.
