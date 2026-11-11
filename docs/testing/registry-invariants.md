# Registry/store and restart invariants (S3/S4)

Base: `d6a4688eb12b542fadb0dfa656285148ee8239aa`. No Exarchos changes. These tests use the actual `ScheduleRegistry` and `ScheduleTickLoop`, a separate small state model, fake time, and synthetic durable storage. No production testing hooks were added.

## Commands

From repository root:

```sh
dotnet run --project src/Bifrost.Tests.Scheduling -- --treenode-filter '/*/*/SystematicRegistry*/*'
dotnet run --no-build --project src/Bifrost.Tests.Scheduling -- --treenode-filter '/*/Bifrost.Tests.Scheduling.Registry/*/*'
dotnet run --no-build --project src/Bifrost.Tests.Scheduling -- --treenode-filter '/*/*/FireFaultEdgeTests/*'
```

The final scoped run has 28 passing TUnit cases in 355 ms, using TUnit 1.2.11, Microsoft Testing Platform 2.0.2, .NET 10.0.11, Debug/net10.0, Fedora Linux 45 x64. Runtime excludes build/restore. Full captured output is in `registry-evidence/final.txt`. The original implementation failed all four initial stale-generation cases and the checkpoint-order case; those regressions were committed before candidate fixes. Eight expanded before/after-effect generation cases also fail against the original registry implementation.

## S3 domain and oracle

Three fixed seeds (17, 83, 2026), each 32 generated histories of 40 commands: 96 histories and 3,840 commands. The generator uses three valid job names, positive integral intervals of 1–60 minutes, a fixed UTC clock, no jitter, and Coalesce policy. Every operation is register, update, pause, resume, or unregister. Membership preconditions follow the independent committed-memory model, including register/unregister failure outcomes. This excludes invalid API calls, cancellation, disposal, and scheduling ticks from the generated domain.

Each command compares a separately computed memory model, durable model, store call attempts, reached durable effects, propagated faults, wake commands, current cadence/state, next-fire calculation, last-fire state, and dispatcher attachment. A failed response after a store effect updates durable state without committing registry state. The model deliberately does not assert transactional rollback of an already-applied external store effect. Idempotent pause/resume do not reach the store. Ten forced histories ensure each of five operations actually reaches both fault boundaries, independent of random coverage.

Two eight-command histories permute the complete alpha and beta operation blocks. Each block preserves its own operation order; there are no specified cross-job dependencies, capacity constraints, or shared mutable application state. Normalized final memory, durable records, counters, and wake multisets must match. Each execution also performs its own model comparisons after every command.

Eight generation races use explicit task-completion barriers before or after the old store effect, covering update/pause/resume and failed registration. The replacement task is invoked before releasing the old operation. In the original implementation, replacement completes synchronously while the old store call is suspended; in the candidate, replacement waits for the mutation gate. No elapsed-time grace determines actor order. The final replacement's cadence and dispatcher must survive, and durable storage must agree. The tests bound unexpected stalls with 10-second timeouts.

## S4 domain and oracle

Six restart cases cross checkpoint success / throw-before-effect / throw-after-effect with dispatcher success / dispatcher exception. They compare durable checkpoint effects separately from dispatch attempts. Occurrence identity is `(job name, scheduled UTC ticks)`, so a replayed occurrence has two attempts but one identity. A lost checkpoint permits replay even after successful dispatch. A persisted checkpoint suppresses replay even when dispatch fails. No exactly-once claim is made.

Four crash-cut cases freeze immutable durable images before/after checkpoint and before/after dispatcher effect, then start a new real tick loop and registry from the image. Dispatcher-cut cases inject checkpoint failure before effects so the image is unacknowledged on either ordering of asynchronous callbacks. The expected model checks whether the occurrence is durable, then predicts zero or one replay. The original process drains only for resource cleanup; its subsequent state cannot mutate the frozen image. This is an explicit synthetic crash model, not OS process termination or database crash recovery. Dispatcher attachment is restored through registration while the saved durable record is restored afterward, and pre-start registration wake commands are drained so they do not overwrite the durable restart schedule.

A separate same-name replacement test creates a stale scheduled heap entry, replaces the job at idle barriers, and proves the old one-minute generation cannot dispatch the new three-minute job early.

Checkpoint ordering is checked with a synchronous router: the store callback must observe a handed-off dispatcher attempt. Two direct fire-path tests invoke the existing private `Dispatch` method by reflection to hold loop recovery out of the observation. A router that throws before or after its dispatcher effect must not record a successful handoff acknowledgment. This tests actual production dispatch behavior without a new production seam. The existing `FireFaultEdgeTests` also pass, including synchronous completion followed by throw and scope-creation failure.

## Candidate fixes and limits

`5a31b52` serializes the five registry mutations using 64 bounded semaphore stripes. Names always map to the same stripe, so old-generation writes cannot cross a replacement. Hash collisions can serialize unrelated names and a slow store can delay colliding jobs; memory remains bounded rather than retaining every name ever seen. Read access and tick-loop fault marking remain outside this protocol. This is a candidate correctness fix with a throughput tradeoff, not a complete linearizability claim for every registry/tick interaction.

`4cc55bc` moves checkpoint invocation after a successful router handoff return, matching `IScheduleStore.RecordFiredAsync`. A thrown handoff can still have performed effects; no checkpoint is claimed in that ambiguous case. Store checkpoint faults remain best-effort. Dispatch completion is not awaited before checkpointing, and application side effects are not transactional with the checkpoint.

These tests cover one process at a time with synthetic single-record persistence, not clustered execution, real database guarantees, cancellation races, backpressure, or exhaustive runtime schedules. The generated S3 histories do not cover cron, one-shots, policies other than Coalesce, or overflowing dates. S4 uses one-minute FireAllMissed intervals and one missed occurrence.

## Replay, reduction, and mutation evidence

`SystematicRegistryTests.Execute` is the actual production/model comparison executor, not a recorded-result stub. `IsValid` rejects deletion candidates that violate membership or interval preconditions. `ReplaySavedHistory` deserializes the shared schema and calls that executor again. A passing permutation also roundtrips through shared replay tooling.

On a generated failure, the shared reducer deletes commands and reduces values/configuration while preserving the actual failure class. It calls the real executor for each candidate and rejects invalid histories. Full source and minimized histories are saved when `BIFROST_REGISTRY_ARTIFACTS` names an absolute directory. Artifacts include the built production assembly SHA, dirty-workspace marker, runtime/build, command order, fault position, clock, observation, and replay command. Dirty=true conservatively marks these local experimental builds; it is not an assertion that every file is modified. Saved source/min pairs for all three seeds and reduction counts are under `registry-evidence/`.

Temporary mutation 1 removed the failed-registration compensation (`this.jobs.TryRemove(name, out _)` in its catch). All three generated seed cases failed `registry-metadata`. Each 40-command source reduced to one valid `register:1` command with interval 1; the fault class was preserved, and the reducer reported one-minimality within its deletion/value/configuration candidate set. The minimized case also replayed successfully against the same mutated production binary. `mutation-compensation.txt` and `replay-mutant.txt` preserve output. The mutation was restored before final validation.

Temporary mutation 2 removed the post-handoff `SafeCheckpoint` call. Nine of twelve then-existing S4 cases failed; three survived because their particular cut/replacement observation did not require that call. This is scoped evidence, not a mutation score for all scheduler behavior. Output is in `mutation-checkpoint.txt`; production was restored afterward.

The original registry implementation was also temporarily restored from `60300c2` and tested against the expanded generation tests; all eight failed. `generation-baseline.txt` captures the run. Both production files have no uncommitted diff after restoration.

To repeat mutation 1, save `ScheduleRegistry.cs`, replace only the compensation line in its catch with `_ = name;`, then run:

```sh
BIFROST_REGISTRY_ARTIFACTS=/absolute/evidence/directory dotnet run --project src/Bifrost.Tests.Scheduling -- --treenode-filter '/*/*/SystematicRegistryTests/GeneratedCommittedHistories'
BIFROST_REGISTRY_REPLAY=/absolute/evidence/directory/registry-17.min.json dotnet run --no-build --project src/Bifrost.Tests.Scheduling -- --treenode-filter '/*/*/SystematicRegistryTests/ReplaySavedHistory'
```

The generated test should fail and the replay should pass because it reproduces the same failure observation. Restore the saved production file and rebuild. To repeat mutation 2, save `ScheduleTickLoop.cs`, replace its post-handoff `SafeCheckpoint` call with `_ = nextFireAt;`, run the restart-test filter, then restore and rebuild. To repeat the original-generation check, save the candidate registry, materialize `git show 60300c2:src/Bifrost.Scheduling/Registry/ScheduleRegistry.cs`, run the race-test filter, then restore and rebuild.
