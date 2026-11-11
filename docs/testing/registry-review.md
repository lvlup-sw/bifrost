# Independent registry and checkpoint review

Reviewed `0fc1213..c021059` in the isolated `registry-review` worktree, including registry mutation stripes, tick handoff/checkpoint ordering, model executors, persisted replay files, and restart tests. No Exarchos changes. No production testing hooks or code comments were added.

## Confirmed findings and fixes

1. **An idle request could acknowledge unprocessed work.** `WaitForNextWakeAsync` read the current time and selected the next timer, then released every pending idle request. A clock advance and new request between those operations were acknowledged before another tick. `SystematicRegistryIdleBarrierTests.RequestDuringParkMustWaitForNextTick` forces that interleaving during timer creation and gates the next clock read. It observes the request's underlying completion source to avoid a second race in the async caller's continuation. The baseline fails with `IsCompleted=True`; the fix passes. Each tick now captures its eligible request count and wake task before processing; late requests wake another tick and cannot be released by the earlier tick. Stop still releases all requests. The request list only appends and removes released prefixes; timeout leaves its request queued, and the API has no cancellation parameter, so no cancellation/timeout path can shift captured prefix eligibility. Commit: `981d6a1`.

   The old `RouterCompletesDispatchThenThrows_InFlightBalanced_LoopRecoversToIdle` failure log reports **zero dispatches**, not a timeout or negative in-flight count. The reproduced premature-idle mechanism explains how this assertion can fail. This is an independently demonstrated race, not proof that the historical failing process took precisely that interleaving. The checkpoint move does not itself cause repeated dispatch: the next occurrence is enqueued before handoff and first-fault recovery has no backoff.

2. **Checkpoint writes could cross a registry replacement.** A synchronous dispatcher can update its cadence or unregister/register the same name before returning to the router. The candidate's new post-handoff checkpoint then wrote the old occurrence and next-fire time into the new durable record. Independently, a suspended asynchronous checkpoint could complete after a replacement and overwrite it. The regression suite fails three of four cases on the candidate: both synchronous mutations and asynchronous before-effect suspension. The asynchronous after-effect case is a passing control.

   The tick loop captures the registry record before handoff. After a successful router return, `ScheduleRegistry.CheckpointAsync` acquires the existing mutation stripe, verifies record reference identity, and awaits the store write under that gate. A superseded record is skipped. An already-running checkpoint completes before an in-process replacement can persist. The router is never called under this gate. This preserves the checkpoint-after-handoff contract, handles synchronous reentrancy, and serializes asynchronous store effects with registry mutations. Commit: `cccb9be`.

## Persistence, cancellation, disposal, and locks

Store failures before effects leave durable state unchanged. Failures after effects can change durability while the registry reports failure and retains/compensates memory. The model correctly distinguishes those outcomes; it does not promise rollback of external effects. The eight original generation races use barriers, not scheduling delays, and now the checkpoint races cover the previously omitted write path.

There are exactly 64 managed semaphore stripes per registry. The same ordinal name always hashes to the same stripe in a process. Collisions serialize unrelated names. Semaphores are intentionally not disposed while callers may still wait/release; they do not create OS wait handles on the paths used here. Their storage remains bounded with job-name churn. Slow persistence, including checkpoint persistence, delays colliding mutations, while tick dispatch remains nonblocking on async checkpoints.

The registry command channel is unbounded and does not allow synchronous continuations. Holding the gate across `PostAsync` therefore adds no capacity/backpressure dependency in the configured channel. Closing the writer can still make posting fail.

New cancellation coverage verifies a cancelled queued waiter never persists and does not leak the stripe. New disposal coverage verifies a waiter queued before disposal rechecks the disposed guard before touching the store. Disposal does **not** drain an already-running store call: that call can commit and subsequently receive `ChannelClosedException` posting its wake. This behavior predates the candidate and is explicitly preserved rather than portrayed as transactional disposal.

Custom store callbacks or synchronous event sinks that await a registry mutation on the held stripe can form a lock cycle. Neither interface promises reentrant mutation support; the built-in event stream publishes through channels. No reentrancy guarantee is claimed for those extension callbacks. Read access, trigger posting, and `MarkJobFaulted` remain outside the mutation protocol. The review does not claim complete linearizability across tick fault marking, external store writers, or multiple processes. A successful checkpoint still does not update the in-memory registry's last-fire record; a later full-record mutation can therefore persist older last-fire metadata. That pre-existing restart/metadata interaction is not covered by the S3 command-only model.

Handoff exceptions before or after dispatcher effects do not checkpoint, and the decorator's single-shot completion guard prevents double decrement after synchronous completion followed by throw. Scope-creation failure also skips checkpoint. Successful asynchronous handoff may checkpoint before dispatcher completion, as allowed by `IScheduleStore.RecordFiredAsync`; this is not exactly-once application execution.

## Actual saved-file replay

S3's existing `ReplaySavedHistory` reads the environment-selected file and invokes the production/model executor. Replaying the checked-in compensation-mutant history against the fixed binary intentionally fails the saved-observation comparison: the actual result has no invariant failure and correctly has one attempted store operation, zero effects, and one propagated fault. `s3-mutant-history-fixed-binary.txt` preserves this negative control; it is not counted as a passing test.

S4 previously had no persisted-file replay entrypoint. `SystematicRegistryRestartReplayTests` adds five shared-schema histories: checkpoint success, throw before effect, throw after effect, and frozen images immediately before/after checkpoint. Every case writes JSON, reads it back, validates the domain, invokes a real registry/tick loop, freezes the durable image at the specified boundary, and starts a second real process fixture. It compares the observed handoff count, durable occurrence, replay count, and original dispatch count to an independent expectation. Artifacts include built source revision, dirty marker, runtime/build, clock, configuration/fault, command order, declared deterministic actor order, and synthetic-crash limitation. The default replay test also roundtrips a temporary file; it never silently returns without execution.

The identical `restart-0-before-checkpoint.json` passes on the restored fixed binary and fails after deliberately moving checkpoint back before handoff: expected handoff count 1, actual 0, failure class `restart-checkpoint-order`. This demonstrates actual component execution, not recorded-result echoing. The temporary mutation was restored, and production files have no uncommitted changes.

```sh
BIFROST_REGISTRY_RESTART_REPLAY="$PWD/docs/testing/registry-review-evidence/restart-0-before-checkpoint.json" dotnet run --project src/Bifrost.Tests.Scheduling -- --treenode-filter '/*/*/SystematicRegistryRestartReplayTests/ReplaySavedRestartHistory'
BIFROST_REGISTRY_ARTIFACTS="$PWD/docs/testing/registry-review-evidence" dotnet run --project src/Bifrost.Tests.Scheduling -- --treenode-filter '/*/*/SystematicRegistryRestartReplayTests/*'
dotnet run --project src/Bifrost.Tests.Scheduling -- --treenode-filter '/*/*/SystematicRegistry*/*'
dotnet run --no-build --project src/Bifrost.Tests.Scheduling
```

## Validation and remaining coverage limits

Actual logs and five replay files are in `registry-review-evidence/`. Scoped validation passes **41/41** cases. The full scheduling suite passes **341/341**, including the original router-completes-then-throws case. The saved-file checkpoint-before mutation and S3 mutant-observation replay are intentional failing controls, separately named. Initial build/run inside the restricted sandbox failed restore without diagnostic errors; successful evidence uses the authorized SDK/NuGet-capable environment.

The S3 generated domain remains valid names, positive minute intervals, Coalesce, command-only operations, and synthetic one-record store effects. The S4 domain remains one occurrence, one process fixture at a time, fake time, synthetic immutable durable images, and deterministic synchronous handoff for persisted replay. Neither is exhaustive scheduler exploration, clustered recovery, real database crash recovery, arbitrary custom store callback reentrancy, or comprehensive cadence/policy coverage. No statistical mutation score is inferred from these targeted controls.
