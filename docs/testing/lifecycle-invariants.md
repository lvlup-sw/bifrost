# Lifecycle invariant evidence

Base: `d6a4688eb12b542fadb0dfa656285148ee8239aa`. All payloads are synthetic. No Exarchos calls, push, or PR.

## Requirements and independent relations

| Requirement | Actual execution and independent oracle |
| --- | --- |
| O1 | 24 deterministic seeds vary pending work from 1–7 behind a gated real worker. Drain cancellation must leave worker cancellation clear. Complete rejects further admission. Graceful completion accounts for admitted identities. Abort/dispose cancel the blocked worker and can leave accepted pending work unexecuted. Capacity-one, zero-worker, blocked admission cancellation and closed asynchronous admission are separate boundaries. |
| O2 | Work identity, outer admission-call identity, inner admission-attempt identity, accepted effect, named worker, execution identity, and handler outcome are separate records. One call with two post-effect failures produces three actual admissions and three distinct executions of the same work identity. Handler failures remain handler outcomes; they do not retry admission. |
| R1 | Actual ResilientOrchestrator wrapper, with an injected inner admission boundary: budgets 0–3, failures 0 through budget+1, pre/post-effect faults (28 cases). Independent expected call bound is min(failures+1,budget+1). Post-effect acceptance is explicitly non-idempotent. Caller cancellation makes one call; a one-second timeout with budget one makes two. The identity test forwards effects into an actual WorkOrchestrator. |
| D1 | 32 deterministic seeds × 48 sequential operations compare real capacity-one DropOldest behavior to a one-slot identity ledger. At quiescence, written = read + retained + dropped. Separate capacity-three early/canceled enumeration checks residual identities. A gated logging callback forces a writer/read overlap at capacity one and checks conservation without assuming which identity survives that overlap. |

Allowed transitions: a call starts one or more admission attempts; each attempt can have an accepted effect even if its caller observes an exception. Accepted effects become pending, then worker executions, then success/failure/cancellation outcomes; abort may leave effects pending. Closing admission is independent of cancellation of a drain wait. Worker completion means registered workers exited, and does not imply every accepted effect succeeded.

## Baseline defect

`lifecycle-baseline.log`: 8 methods, 7 passed, 1 failed, 3.021 seconds. The failing D1 witness writes seed, starts a next write until its warning, drains seed, releases the writer, then drains next. Written=2, read=2, retained=0, but DroppedCount=1. The pre-write fullness check incorrectly records an eviction even when a concurrent reader makes space.

`lifecycle-counterexample.json` preserves the real commands, source SHA, runtime/build, actor ordering, invariant relation, and observation. The test round-trips the shared ReplayRecord and reexecutes the real component; HistoryReducer only accepts domain-valid candidates preserving the failure class. This minimal three-operation domain cannot delete an operation without invalidating the race. Reduction therefore demonstrates validity and failure preservation, not a large reduction ratio.

## Reproduction

Run `dotnet run --project src/Bifrost.Tests -- --treenode-filter '/*/*/Lifecycle*/*'` from the worktree. Package/build access required escalation in this environment. `lifecycle-mutations.py` records commands, exits, and complete scoped output, and restores mutated production text in finally blocks.

## Limits

These are bounded histories, not proofs over all schedules. The real logger gate deterministically controls the D1 overlap; other worker scheduling is only constrained by handler entry/release. Timeout tests use actual one-second wall-clock time, not a virtual scheduler. Retry budget arithmetic uses an injected admission boundary plus one end-to-end real queue witness. The synchronous shared replay/reducer interface requires a narrowly scoped VSTHRD002 suppression in EvaluateRace; queue drain operations are nonblocking, and worker/logging waits have five-second bounds. No behavioral analyzer is disabled globally. Documentation diagnostics are suppressed only in the new test files to honor the no-added-comments requirement.

## Mutation sensitivity

The temporary drain-to-abort mutation fails O1's assertion that canceling a drain wait leaves worker cancellation clear. Increasing configured retries by one fails R1's independent inner-call bound. Replacing DropOldest with DropWrite fails the D1 retained-identity check. Logs retain each command and exit. A DropNewest probe survived: at capacity one, dropping the newest buffered item and dropping the oldest buffered item are the same operation. That equivalent probe is retained as a limitation, not counted as a killed mutant. All production mutations were restored before the test commit.
