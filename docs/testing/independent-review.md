# Independent review of invariant tests and candidate fixes

Reviewed the integrated tree at `24fbfd5b729fa3a28925da3128c3a5e94a9aa847` against baseline `0fc121384950d964722a8de36d72a26679a428d3` in an isolated worktree. Read the component contracts, test oracles, reducers, saved histories and mutation logs. This review does not certify later registry/dispatch work or tool pilots, which were being developed separately. No Exarchos, push or PR. No source comments were added.

## Findings resolved

1. **Disposed autoscaling state was incorrect.** `AutoscalingEngine.Dispose` released the timer without clearing `IsRunning`; `StartAsync` then returned success through its running fast path. The interface defines `IsRunning` as whether the engine is running. `DisposeClearsRunningStateAndPreventsRestart` failed against the integrated candidate, as retained in `independent-dispose-baseline.txt`. Disposal now shares the lifecycle lock, records disposal and clears running state; Start rejects a disposed instance. Stop after disposal and repeated Dispose remain safe. The focused autoscaling suite passed 143 cases (`independent-autoscaling-fixed.txt`). This is not a new guarantee that synchronous Dispose waits for an already entered callback.

2. **D1's saved history was not consumed by its replay command.** The original `D1_ConcurrentDropAndRead_RealReplayAndReduction` constructed a fixed history, and its replay/reduction branch only ran after a failure. On fixed production it therefore tested the race but did not read the committed artifact. `D1_SavedCounterexampleExecutesPersistedHistory` now always reads the JSON, validates the domain, executes the recorded commands against the real queue, and checks either the fixed invariant or exact saved failure. The committed command points to this entry point, and newly captured failures use actual source provenance and a saved-file command. `independent-d1-exact-baseline.txt` records exact failure-class/expected/actual equality with only DeadLetterQueue restored from the baseline SHA; other source remained the candidate. `independent-d1-fixed.txt` records five D1 cases on the fix. The baseline source was restored in a finally block after the experiment.

3. **O2's initial identity test did not independently observe all claimed outcomes.** Its handler generated the worker/outcome labels it later asserted. That test usefully checks admission-attempt-to-handler-invocation identity, but those labels alone did not establish production worker-exit or outcome counters. `LifecycleOutcomeInvariantTests` adds gated success, failure and cancellation histories using the production completion-tracking handler decorator. It observes real completion events, busy/idle callbacks, PendingCount, ActiveWorkers and Completion before and after worker exit. It distinguishes a pending abandoned item, a rejected admission, a failed handler and a successful worker shutdown. It complements the existing real retry/admission identity test. The scoped lifecycle run passes 13 cases (`independent-lifecycle-fixed.txt`). A temporary production mutation publishing failed completion events as successful fails both failure and cancellation cases while the success case passes (`independent-o2-outcome-mutation.txt`); the mutation was restored.

## Production conclusions within reviewed scope

The Coalesce interval shortcut implements the inclusive latest-due arithmetic for positive intervals and zero jitter. Multiplication is bounded by the representable elapsed interval; it does not enumerate the backlog. Added probes cover differing UTC offsets, the full UTC DateTimeOffset range with a one-tick interval, and a non-advancing custom cadence. All 13 interval cases passed (`independent-scheduler-checks.txt`). The generic path terminates on null, future or non-advancing occurrences but remains O(backlog) for advancing custom/cron/jittered cadences. Date arithmetic whose result cannot be represented in the retained local offset remains outside the tested domain. No universal overflow or runtime bound is claimed.

The autoscaling callback barrier closes the observed Stop snapshot race: callback admission and the running flag share a lock; Stop first disables further admission, waits for entered callbacks to publish their scaling tasks, then drains those tasks. The existing gated regression tests this order. A restart after awaited Stop is tested. Simultaneous Start during an outstanding Stop and synchronous Stop waiting from inside its own event callback do not have a documented ordering/reentrancy contract; they are not evidence of supported behavior. The added Dispose fix prevents future callback admission without pretending to wait synchronously for active callbacks.

The DLQ fix counts actual channel evictions instead of predicting them before TryWrite, and reads occupancy from the channel rather than maintaining a racing second counter. The deterministic reader/writer witness still checks the same conservation relation even though logging now occurs after eviction. Sequential identity checks and concurrent conservation are complementary; the logger gate is not a general scheduler or a proof over all schedules.

## Catalog evidence map

The catalog at the reviewed SHA contains 17 families. Its stale statuses and pending-integration text must be reconciled by the integration owner; this review does not convert all items to complete based on test totals.

| Family | Inspected evidence and boundary |
|---|---|
| Q1 | Generated successful-effects multiset, multiplicity/payload/priority snapshots and separate Clear accounting; persisted generated/reduced Clear mutant replay. |
| Q2 | Capacity boundaries, rejected admission snapshot, remove/refill and Clear/fresh capacity; production mutation evidence. |
| Q3 | Quiescent best-observed minima with lawful forward/reverse comparers, reference/value priorities and ties. Does not assert stable tie order. |
| Q4 | Fault sweep counts attempted versus reached comparisons, validates rollback and recovery drains/refills. One stripe makes comparison paths reproducible. The separate replay adapter's 1..32 sweep is not exhaustive. |
| Q5 | Barrier histories establish no invented/duplicated successful removals and final conservation. Six serialized actor schedules establish observed-empty results without overlap. Overlapping false-return permitted-history checking and replay of internal publication schedules are not supplied by these tests. |
| P1 | Independent BigInteger unit oracle and timestamp translation over exact representable units. Rounding and overflow outside this declared domain are not exercised. |
| P2 | Independent floor-based threshold table and urgency ordering at adjacent occupancy boundaries. |
| O1 | Gated real workers distinguish close, drain-wait cancellation, abort and disposal; blocked admission/zero-worker boundaries included. |
| O2 | Real post-effect retry/admission ledger plus new independent production outcomes, worker state transitions and exit counts for success/failure/cancellation. |
| R1 | Budgets 0..3, failures around budget, before/after-effect admissions, caller cancellation and real timeout call counts. A timeout is wall-clock based, not virtual-time replay. |
| D1 | Capacity-one identity model, early/cancelled drain, forced overlap, concurrent conservation, and now actual persisted baseline/fixed replay. Capacity-one DropNewest mutation is equivalent to DropOldest and was correctly retained as a surviving equivalent control. |
| S1 | Tick-exact generated phase/progress checks, translations and earlier/equal/future/null last-fire states; jitter/unrepresentable arithmetic excluded. |
| S2 | All policies, cap-adjacent/larger backlogs, startup, exact baseline replay/reduction, and added offset/extreme/progress probes. Model-only negative control is not counted as a production mutation. |
| S3 | Not present in the reviewed base; registry worker owns later evidence. Requires separate inspection of commit boundaries, store failures and stale-generation races. |
| S4 | Not present in the reviewed base; registry worker owns later evidence. Requires separate inspection of restart occurrence identity and crash boundaries. |
| A1 | Independent worker-limit/watermark table plus fake-clock cooldown before/equal/after boundary. |
| A2 | Entered-callback Stop regression; fake-timer failed-action recovery, awaited stop/restart and disposal; added disposed-state regression. No claim of arbitrary lifecycle interleaving/reentrant Stop coverage. |

## Replay and counting limits

The shared reducer checks validity before evaluation, preserves the exact failure class and uses a strictly decreasing measure. Its one-minimal flag applies only to its candidate transforms and deterministic executor; it does not establish globally minimal histories. Mutable input callbacks are explicitly outside its contract. Queue replay does not recreate internal random striping or an overlapping OS schedule. Serialized actor order and native concurrent stress are distinct evidence.

`SchedulerReplayTests.ReduceProductionBaselineWhenRequested` returns without executing unless `BIFROST_REDUCE_BASELINE=1` (line 47 at the reviewed base). Ordinary reported 2/2 scheduler replay results contain one actual saved replay and one dormant reduction entry point. Baseline reduction is supported by the separately retained baseline logs, not by that default test count. Likewise the fixed D1 original method exercises its race oracle but not its failure-only reducer branch; the new saved-record method supplies unconditional persisted replay.

Tool trials, CI discovery/retention, mutation adequacy across all 17 families, and later S3/S4 changes require integration audit. A passing suite, source inspection, or a model-only control is not interchangeable with production mutation sensitivity.
