# testgen + test-campaign qualification (issue #111)

Qualification of `@lvlup-sw/exarchos@2.14.0` (`testgen` / `testgen-mcp`)
with the shipped `test-campaign` skill (`0.2.0`) on Bifrost at
`3cbc7b9ca220b8cc47443a06847a909da0d8f57a` (current remote `main` at run start).
TUnit `1.2.11`, Vitest `3.2.7`, .NET SDK `10.0.303`, runtime `10.0.11`,
Node `v24.12.0` / npm `11.10.1`, Linux x64 (Fedora 45, kernel 7.2.15), 32 CPUs.
Package integrity: `sha512-2sLVoz03+WOqUFMnsPVNQC0CGF6wfXv+S5kc9QE7wYow896know7mPJxXryrN26lex+1B+GGvWDWoncFMtPdTw==`,
gitHead `2a55a68eeb257abe2f4b73908beca70c2bb3db35`.
Raw logs, requests, and measurements live in the external run root
(`$RUN_ROOT`, archived outside the repo); this file keeps the evidence matrix.
Skill source: `tools/node_modules/@lvlup-sw/exarchos/rendered/skills/standard/test-campaign/`
(`SKILL.md` sha256 `d47c2352…`, `dimensions.md` `e8b4c247…`, `generation.md` `8278c345…`).
It was copied to `.agents/skills/test-campaign/` for the run and removed at cleanup.

Bifrost supports in-memory behaviour only. Durability / restart recovery is
**not applicable: unsupported today** (#110), not a defect in this run.

## Campaign table (written before executing the suites)

Budgets (runbook §5): stateless 32 cases/suite, 256 construction attempts;
stateful 12 histories, 24 steps/history, 192 candidate attempts; reduction 96
attempts; three recorded seeds for seeded probes (maintained suites freeze one).

| # | Method / property | Accepted contract | Domain | Independent oracle | Reach condition | Negative control (intended fault) | Native command | Replay material |
|---|---|---|---|---|---|---|---|---|
| P1 | Property `fifo/sequential-conservation` | `FifoChannelWorkQueue<string>` preserves an accepted sequence through enqueue → `Complete()` → drain, sequential calls, capacity ≥ length | `array<string>`, alphabet `abéあ😀`, elem len 0–8, array len 0–12; explicit empty/duplicate/Unicode cases | Observed drained sequence equals the input sequence element-for-element (ordinal) | Every accepted item dequeued exactly once; queue empty after drain | Drop an extra item in `TryDequeue` (`faults/fifo-drop-extra.patch`) | `dotnet run --project src/Bifrost.Tests -c Release -- --treenode-filter '/*/*/FifoPropertyTests/*'` | `TESTGEN_REPLAY` case from fault run |
| M1 | Metamorphic `coalesce/translate-by-interval` | Zero-jitter interval `Coalesce`: translating both timestamps by Δ=interval translates the selected occurrence by Δ | Record `{last,now,interval}` integer ticks; ≥1 due occurrence; translated instants representable | Relation `after − before = Δ`, both observations valid occurrences | Selected occurrence is the latest due occurrence (see limits: translation alone cannot detect earliest-selection) | Earliest-occurrence selection (`faults/coalesce-earliest.patch`) — expected **survivor** for this relation, killed by M2/D1 | `dotnet run --project src/Bifrost.Tests.Scheduling -c Release -- --treenode-filter '/*/*/CoalesceTranslateTests/*'` | `TESTGEN_REPLAY` (from M2/D1 fault) |
| M2 | Metamorphic `coalesce/advance-one-interval` | Advancing the query by one interval advances the selected latest occurrence by one interval | Same record domain; `now+interval` representable | Relation `after − before = interval` | ≥1 due occurrence before and after the advance | Earliest-occurrence selection | `… '/*/*/CoalesceAdvanceTests/*'` | `TESTGEN_REPLAY` case |
| D1 | Differential `coalesce/exact-enumeration` | `Coalesce` returns the single latest occurrence `last + ⌊(now−last)/interval⌋·interval`, or none | Same record domain; due count bounded ≤ 64 for the enumerator | Separately authored bounded occurrence enumerator (steps one interval at a time; never calls the production selector) | Enumerator reaches the same latest occurrence | Earliest-occurrence selection; (probe) exclusive upper bound | `… '/*/*/CoalesceDifferentialTests/*'` | `TESTGEN_REPLAY` case |
| S1 | Stateful `worker-registry/sequential-snapshot` | `WorkerRegistry` create/idle/busy/stop/finish/ID-reuse; snapshot partitions total/active/idle/busy/stopping; stop-requested workers are not idle | Operations `create/idle/busy/stop/finish` over enum ids `a,b,c`; 1 explicit + generated histories | Independent dictionary model (no production code); per-worker state + snapshot counts compared after initial state and every step | Worker delegates finish only on explicit release; ID reuse after finish | Count stop-requested workers as idle (`faults/worker-idle-count.patch`) | `dotnet run --project src/Bifrost.Tests -c Release -- --treenode-filter '/*/*/WorkerRegistryCampaignTests/*'` | `TESTGEN_HISTORY` + `TESTGEN_REPLAY` history |

Fixed expected examples run alongside the relations (D1's enumerator is exact;
handwritten fixed Coalesce/Fifo examples already exist in
`IntervalInvariantTests` / `FifoChannelWorkQueueTests` and are re-run).
Shared-assumption risk: M1/M2/D1 all assume occurrences align to whole
intervals from `lastFiredAt` (the `IntervalCadence` contract); D1's enumerator
shares that alignment assumption but not the production closed-form selector.
PR #109's WorkerRegistry binding was inspected at its reviewed head
`17f67d4ac2e5083191ff98102a66073611b8b51f` in a separate worktree and is
reported separately below; S1 is a fresh binding in the `Campaign` namespace
authored for this run (the new non-arithmetic binding is P1's Fifo binding).

## Evidence matrix

Outcomes are **pass / fail / indeterminate** for the stated scope only.
Harness failures (build errors, timeouts, unreached faults) are never
reported as application failures, and rejected/exhausted exploration is
never reported as coverage. Request hashes: `fifo cc741699…`,
`worker-registry f61a1b20…`, `coalesce-translate 22c03822…`,
`coalesce-advance e65b5d5d…`, `coalesce-differential 03b23374…` (sha256).
Fault hashes: `fifo-drop-extra 70d1c5cf…`, `coalesce-earliest 54026339…`,
`coalesce-exclusive 162df490…`, `worker-idle-count e4253587…`.
No unmeasured productivity percentage is claimed; generation/native times
below are elapsed tool times, binding authoring was not separately clocked
beyond the session log (bindings + requests were authored before any suite
ran, generated suites passed after one binding-domain repair, see below).

| Row | Meaning / oracle | Reach | Sensitivity (control) | Replay | Limits | Outcome |
|---|---|---|---|---|---|---|
| P1 Fifo property | Drained sequence = accepted sequence (new binding) | 24/24 native passed; explicit empty/duplicate/Unicode + 16 generated (attempts 18, duplicates 2, not exhausted; 3 seeds probed, identical-repeat patch byte-equal) | `fifo-drop-extra` killed: `property/oracle-false`, reduced case `[["",""]]` (attempts 17, dup 8) | Replay at fresh target reproduced the same signature under fault; restored replay + suite passed | Sequential only; capacity ≥ length; no concurrency claim | **pass** |
| M1 Coalesce translate | Occurrence translates by Δ=±interval | 26/26 passed | Earliest fault **survived** (expected: translation holds for earliest too) — classified survivor, not a kill | No oracle failure to replay; replay covered by M2/D1 same fault | Relation cannot detect earliest/exclusive faults alone; paired with D1 | **pass** (relation), survivor recorded |
| M2 Coalesce advance | Latest occurrence advances by query step | 26/26 passed | Earliest fault killed: `metamorphic/oracle-false`, case `{steps:1,remainder:0,shift:0}` | Same signature at fresh target; restored passed | Encoded domain (interval fixed 100000 ticks); calendar-end cases are in existing fixed tests | **pass** |
| D1 Coalesce differential | Production vs independent bounded enumerator | 26/26 passed; coverage probe: interval-Coalesce lines reached (file line-rate 0.30 overall; Cron/other policies unreached by design) | Earliest killed (`differential/oracle-false`, `last=0,now=300000,interval=100000`) and exclusive-upper-bound killed (reduced `last=100,now=300100,interval=100000`) | Both replayed with same signature; restored passed | Enumerator shares interval-alignment assumption; due count ≤ 64 (encoded ≤ 32 for M1/M2) | **pass** |
| S1 WorkerRegistry stateful | Dictionary model vs per-worker state + snapshot partitions | 12/12 passed (1 explicit + generated; histories record attempts/rejected, e.g. rejected disabled steps filtered); explicit disabled history probe rejected as `stateful/precondition`, not passed off as coverage | Idle-count fault killed: `stateful/stop/state-mismatch`, reduced `create(b), stop(b)` (attempts 7, rejected 3, dup 1) | `TESTGEN_HISTORY` + replay reproduced the history/signature; restored passed; disposal in `finally`, 5 s cleanup bound | Sequential histories only; no linearizability/durability claim | **pass** |
| Operations | All 6 operations described; `init` ×4 methods, direct `scaffold_tests` with distinct stateful operation domains, patch + `--json` generation, create-only `git apply --check` | Throwing scaffold stubs failed with `NotImplementedException` when exercised; generation alone modified no files and ran no target code | v1 property (PriorityKey, 4 cases) and v1 metamorphic (archived Coalesce binding) generated; differential/stateful correctly require v2 | Direct `replay_tests` (MCP) succeeded for a valid record and rejected property-ref and domain mismatches (`INVALID_INPUT`) | Legacy v1 generation was patch-only (its maintained suites already run natively) | **pass** |
| Boundaries | 34 probes: boolean/number/enum/nullable/record/nested-array domains, exact integers beyond JS safe range (`9007199254740993`, `±9223372036854775807/8`), reversed bounds, duplicate enum/alphabet, unknown record field, `__proto__`, lone surrogate, supplementary + U+0085/U+2028/U+2029 alphabets, depth 12, 257 cases / oversized budgets | At-limit values accepted; beyond-limit and malformed inputs returned structured `INVALID_INPUT` (exit 2) or `UNSUPPORTED_FRAMEWORK` (exit 3) with stdout/stderr separated; U+2028-family alphabets were escaped in emitted C#, not emitted raw | First nested-array probe case was authored outside its domain and was correctly rejected — a probe (binding) defect, fixed and re-passed | Determinism: identical request/seed → byte-equal patch+fixture; changed seeds need not differ in small domains | Empty `cases: []` is accepted in v2 (generation fills it) per the installed schema | **pass** |
| MCP transport | Real MCP stdio client: initialize → tools/list (6 tools) → tools/call for all 6; stdout was clean JSON-RPC only; tool errors set `isError`; unknown method returned protocol error `-32601` distinct from tool errors | All 4 generation tools with valid per-method requests | Invalid framework via MCP set `isError` with `UNSUPPORTED_FRAMEWORK` | MCP patches were byte-equal to CLI patches for all 4 methods | Optional transport in the runbook; exercised anyway | **pass** |
| Vitest 3.2.7 (2nd emitter) | Disposable synthetic TS fixture outside Bifrost (no TS stack added to Bifrost): FIFO queue, TS Coalesce, mini registry | Baseline 72/72 passed (20+20+20+12), strict `tsc --noEmit` clean; labelled synthetic-fixture evidence, separate from real Bifrost evidence | Drop-item, earliest, and idle-count faults each killed (`property/oracle-false`, `metamorphic/oracle-false`, `stateful/stop/state-mismatch`) | Each replayed at a fresh target with the same signature; restored baselines passed | Synthetic target only; does not certify the TS emitter for real apps | **pass** (synthetic) |
| PR #109 (separate) | WorkerRegistry binding at reviewed head `17f67d4…`, applied in a separate pinned worktree (base `3cbc7b9` + PR diff; direct fetch was unavailable, SSH denied), reported separately, not mixed into this branch | 12/12 histories passed | Its control reduced to `create(a), stop(a)`, `stateful/stop/state-mismatch`, replayed, restored | Control script exited 0 | Does not establish concurrent safety or durability (its own stated limits) | **pass** (separate revision) |
| Lifecycle / concurrency | Existing 27 invariant suites (`scripts/run-invariant-tests.py`), incl. lifecycle, queue, registry race/restart suites | 27/27 passed, 0 skipped in the runner summary | Existing mutation/lifecycle controls in those suites (unchanged) | Existing replay fixtures (`SchedulerReplayTests`, queue replays) passed | Seeds do not reproduce OS schedules; sequential S1 is not a concurrency proof | **pass** (bounded) |
| Overload (bounded lane) | Declared before running: Fifo queue capacity 128, 4 producers × 12 500 items via `WriteAsync`, 4 consumers, 60 s deadline/cleanup cap, exact count+checksum accounting; **not** sustained production qualification | Completed in ~3 s, 50 000/50 000 items, checksum exact (temporary probe test, removed after the run) | Capacity rejection is covered by maintained contract tests | Probe log retained externally | No memory/latency thresholds beyond the 60 s cap; no fairness claim | **pass** (bounded) |
| Coverage paired with oracles | MTP coverage (cobertura) for Fifo (0.89 line on `FifoChannelWorkQueue.cs` from P1 alone), WorkerRegistry, and Coalesce suites | Reached interval-Coalesce, FIFO enqueue/drain, registry create/stop/finish paths | Coverage was never used as correctness proof; every row above pairs reach with a fault control | Cobertura files retained externally | Missed branches are the other cadence policies / priority queues, out of these rows' scope | **pass** (reach only) |
| Full gate | `dotnet build src/Bifrost.sln -c Release` (0 warnings/errors); full solution test run | 1686/1687 on the first parallel run: one unrelated timing timeout in `EventStreamOrchestratorTests.PublishToSubscribers_WithFullSubscriberBuffer_RemovesStaleSubscriber`; it passed alone and the full `Bifrost.Tests` project passed 1032/1032 on rerun | Campaign suites are 114 of the total (24+26+26+26+12) | — | That timeout is **indeterminate** (harness/timing), not an application failure attributed here | **pass** with one indeterminate flake noted |

### 13 test-campaign dimensions

Examples/partitions (explicit boundary cases in every request), properties
(P1), metamorphic (M1/M2, with M1's survivor classified), differential (D1),
stateful/model (S1), coverage/conditions (coverage row), static/solver (not
selected: no finite solver claim was needed for these closed-form oracles;
the exact enumerator is the independent check), fuzzing/configurations
(seeded generation + 34 boundary probes), concurrency/lifecycle (existing
invariant suites, run separately from the sequential model),
durability/recovery (**not applicable: unsupported today**, #110 — not a
defect), load/resources/liveness (bounded overload lane), mutation/negative
controls (4 reviewed fault patches, survivors classified), reduction/replay
(every kill replayed at a fresh target, then restored and re-passed).

### Defect classification

- **Bifrost defects:** none found. All baselines passed on `3cbc7b9…`.
- **testgen defects:** none filed. Structured errors, exit mapping (1/2/3),
  determinism, escaping of U+0085/U+2028/U+2029, replay-mismatch rejection,
  and MCP/CLI parity all matched the installed schemas. Observations that
  are documented behaviour, not defects: generation does not consult the
  binding `IsValid` (rejected cases fail explicitly), and any stateless
  operation accepts the shared v2 stateless request shape (binding methods
  are only checked natively).
- **Binding/oracle defects (this run, fixed):** the first Coalesce domain
  let random structural cases fall outside the binding precondition
  (22 rejected) and let metamorphic follow-ups leave the declared domain.
  Fixed by guaranteeing due-count bounds structurally and by the encoded
  `{steps,remainder,shift}` metamorphic domain; no assertion was weakened.
- **Unresolved / indeterminate:** the one unrelated full-run timing timeout
  above; other platforms (only Linux x64 ran here — CI in this repo is also
  Linux-only, so no hosted matrix evidence exists for other platforms and
  none is inferred); Exarchos settlement — no prepared Exarchos task/capsule
  exists for this run, so no settlement reference was invented and native
  reports stay external.

### Regenerate and run

```sh
npm install --prefix "$RUN_ROOT/tools" --ignore-scripts --no-audit --no-fund \
  --@lvlup-sw:registry=https://registry.npmjs.org/ @lvlup-sw/exarchos@2.14.0
export PATH="$RUN_ROOT/tools/node_modules/.bin:$PATH"
testgen generate_property_tests    --input src/Bifrost.Tests/Systematic/Testgen/Campaign/fifo-property.testgen.json
testgen generate_metamorphic_tests --input src/Bifrost.Tests.Scheduling/Testgen/Campaign/coalesce-advance.testgen.json
testgen generate_differential_tests --input src/Bifrost.Tests.Scheduling/Testgen/Campaign/coalesce-differential.testgen.json
testgen generate_stateful_tests    --input src/Bifrost.Tests/Systematic/Testgen/Campaign/worker-registry.testgen.json
# (translate uses generate_metamorphic_tests on coalesce-translate.testgen.json)
# Review the create-only patch, then: git apply <patch>
dotnet run --project src/Bifrost.Tests -c Release -- --treenode-filter '/*/*/FifoPropertyTests/*'
python3 scripts/testgen-campaign/run-controls.py --testgen "$(command -v testgen)" --output "$RUN_ROOT/controls"
```

Maintained files: bindings + requests + generated suites under
`src/Bifrost.Tests/Systematic/Testgen/Campaign/` and
`src/Bifrost.Tests.Scheduling/Testgen/Campaign/`, the fault fixtures under
`docs/testing/evidence/testgen-campaign/faults/`, and
`scripts/testgen-campaign/run-controls.py`. Raw logs, coverage, requests,
and the Vitest fixture were archived outside the repository (external run
root, 4.5 MB, tool `node_modules` excluded). The temporary
`.agents/skills/test-campaign/` installation was removed and the PR #109
worktree was deleted; the source tree is clean except the files above.
#85 stays open for its broader risk assessment; this run adds the evidence
above without closing it.
