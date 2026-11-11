# Production history replay and reduction

Run from the repository root with .NET SDK 10.0.303 (recorded runtime .NET 10.0.11, Debug/net10.0, TUnit 1.2.11):

```sh
dotnet run --project src/Bifrost.Tests.Concurrency -- --treenode-filter '/*/*/QueueReplayTests/*'
dotnet run --project src/Bifrost.Tests.Scheduling -- --treenode-filter '/*/*/SchedulerReplayTests/*'
```

The queue suite passed 6/6. The scheduler suite passed 2/2, including one baseline reduction entry point that intentionally returns without execution unless `BIFROST_REDUCE_BASELINE=1`. The saved scheduler counterexample and saved queue counterexample execute unconditionally in these runs. The queue suite also verifies malformed nested commands are rejected as InvalidDataException, and writes/reads temporary Q4, Q5 and paired MR records before executing their restored semantics.

## Scheduler production negative control

An isolated worktree at unchanged production SHA `0fc121384950d964722a8de36d72a26679a428d3` received only the new test adapter, shared test helpers and test project Compile link. Its production code was not edited. Dirty=true records these test additions. The reducer executed the real `MissedFirePolicyApplier.ComputeMissedFires`, starting with 500 periods, interval 17 ticks and last-fire 7919 ticks. Its independent oracle computes the inclusive latest missed occurrence with integer arithmetic.

The validity predicate retains one coalesce command, positive interval, representable clock and at least one missed period. Command deletion and zero periods are invalid. The count simplifier tries every smaller positive count; numeric configuration simplification uses the shared reducer. The final history is 101 periods, interval 1 tick, last-fire 0, logical now 101. The original and reduced failure class is `S2.latest-missed`: expected 101, actual 100. The test asserts one-minimality within these candidate transformations, not global minimality across all possible cadences.

The committed `src/Bifrost.Tests.Scheduling/scheduler-coalesce-replay.json` is the actual reducer output. `replay-scheduler-baseline.txt` records successful reduction against baseline. `replay-scheduler-exact-baseline.txt` records exact equality of saved failure class, expected and actual on baseline with `BIFROST_REPLAY_EXPECT_FAILURE=1`. `replay-scheduler-fixed.txt` records that current production returns the saved expected occurrence. A clean checkout runs the command embedded in the JSON; no external workflow service is involved.

To repeat reduction, create an isolated worktree at the baseline SHA, copy the current `HistoryTools.cs`, `SchedulerReplayTests.cs`, and test csproj into their corresponding paths, and run there:

```sh
BIFROST_REDUCE_BASELINE=1 BIFROST_REPLAY_FILE=/tmp/scheduler-reduced.json dotnet run --project src/Bifrost.Tests.Scheduling -- --treenode-filter '/*/*/SchedulerReplayTests/ReduceProductionBaselineWhenRequested'
BIFROST_REPLAY_EXPECT_FAILURE=1 BIFROST_REPLAY_FILE=/tmp/scheduler-reduced.json dotnet run --project src/Bifrost.Tests.Scheduling -- --treenode-filter '/*/*/SchedulerReplayTests/SavedProductionCounterexample'
```

## Queue semantic histories

The additional generator materializes 300 offer/take/clear commands for each of 32 seeds and replays each at buffer capacities 0, 1 and 16 against the real ConcurrentPriorityQueue. It persists full semantic commands, configuration, actual/expected observations and source metadata on failure. A seed is not required for replay. Q1 sequential failures also run the shared reducer and save a separate minimized record. Existing broader GeneratedQueueInvariantTests remain unchanged, including their Q1–Q5, fault-position enumeration, concurrent barriers and all five metamorphic relations.

In a separate isolated worktree at `349de01c1a3d4c27a00233cc5acdd24abfb90d42`, `ConcurrentPriorityQueue.Collection.cs` was changed only from `int removed = t.LockedClear();` to `int removed = 0;`. This deliberate production mutation leaves items behind after Clear. It is not a newly discovered defect in current production. The real generated run failed at command 3, persisted all 300 commands, then reduced to offer+clear in 353 evaluations. Failure class remained `Q1.snapshot`; the reduced expected snapshot was `0|`, actual `1|main:278:0; command=1`. Capacity and stripes reduced to 1, buffer and priority to zero. Exact replay on the mutant passed in failure-expectation mode; ordinary replay on unchanged production passed.

`docs/testing/replay-queue-source.json` retains the actual full generated failure. `src/Bifrost.Tests.Concurrency/queue-clear-replay.json` retains the actual reduced failure. Their ReplayCommand metadata was made repository-relative after copying so it runs from a clean checkout. Both record the isolated source SHA and dirty=true. Logs are `replay-queue-control.txt`, `replay-queue-exact-control.txt` and `replay-queue-fixed.txt`.

```sh
BIFROST_REPLAY_FILE=docs/testing/replay-queue-source.json dotnet run --project src/Bifrost.Tests.Concurrency -- --treenode-filter '/*/*/QueueReplayTests/ReplaySavedHistory'
```

For new failures, `BIFROST_REPLAY_OUTPUT` selects the artifact directory (default `/tmp/bifrost-replays`). Generated records print their file path and embed the replay command. `BIFROST_REPLAY_EXPECT_FAILURE=1` requires exact agreement with the saved failure observation; ordinary mode requires the invariant to pass.

Q4 histories contain an explicit `fault` command whose Value is the one-based comparer invocation to throw on during the next operation. FaultPosition is the zero-based command index of that arming command. Recovery drains are also commands, so replay restores both injection and recovery behavior. Q5 histories record actor IDs and their serialized command order for all six three-actor permutations. MR records contain both the source history and translated followup, with the translation in Configuration; replay checks each against the live multiset model and compares normalized drain results. Other existing MR families remain covered by the unchanged original tests, but are not yet separately serialized by this adapter.

## Boundaries

These adapters do not replay internal random stripe selection or overlapping native thread schedules. The controlled Q5 adapter executes command-level actor ordering sequentially; it does not claim to reconstruct races. The original barrier tests still exercise overlap, but their internal schedules are not persisted. Queue comparison validates legal observable outcomes rather than forcing an arbitrary tie order; translated generated samples use distinct priorities. The independent fault sweep remains the original Q4 test; this adapter's finite 1..32 positions are not claimed to exhaust all reachable comparer calls. No existing broad test was removed or weakened. Scheduler jitter and custom cadence semantics remain outside this replay domain. Temporary negative controls were confined to separate worktrees; candidate production code is unchanged.
