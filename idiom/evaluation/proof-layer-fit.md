---
repo_path: "/home/reedsalus/.codex/worktrees/92f2/bifrost"
target_kind: "codebase"
revision: "3a17ed0f45dfd64d953712ccda286b0915bef838"
base_revision: null
target_ref: "whole repository"
cost_setting: "8 ranked surfaces; stop at two consecutive surfaces with no surviving obligation"
scope_rule: "Eight surfaces ranked by consequence x exposure (survey.md); stopped at rank 8 by budget exhaustion. Two-consecutive-no-surviving-obligation early-stop condition was not established."
capability_floor: {"tfm": ["net10.0"], "lang_version": "latest (C# 14 with SDK 10.0.303); benchmark driver 14.0", "nullable": "enable", "implicit_usings": "enable", "analyzers": "enable", "analysis_level": "latest", "cpm": "true for src and samples; driver unset with no direct package dependencies", "test_host": "mtp (TUnit 1.2.11)", "sdk": "10.0.303 evaluated; global.json 10.0.100 latestMinor", "treat_warnings_as_errors": "true after restore for src/samples; driver false"}
updated: "2026-09-17"
skipped: "No lens skipped. Survey/inventory single-agent checklist; fresh-context evaluation ensemble. No exhaustive algorithm/performance verification. External CI implementation not fetched."
scope_answer: null
scope_assumption: "The whole repository, no additional user-named external sources. Scope question asked; no answer received. This is an assumption, not an author quote."
external_references: [{"path": "https://learn.microsoft.com/en-us/dotnet/standard/datetime/timeprovider-overview", "why": "Validate TimeProvider timer and fake-time replacement."}, {"path": "https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing", "why": "Validate nuget.org OIDC replacement for stored publishing key."}, {"path": "/home/reedsalus/.nuget/packages/lvlup.build/1.4.0/build/Lvlup.Build.props", "why": "Resolve imported effective analyzer and warnings-as-errors settings after restore."}]
---

# Stage 3 — Proof-layer fit

Fresh-context evaluation of the nine draft obligations. This lens read the ledger, survey, obligation evidence, proof ladder, guard discipline, relevant source/workflows, effective floor, build log and local test logs. Scope remains assumed whole-repository scope; no author scope answer was supplied. Evaluation concerns below are not synthesis classifications. Product files were not changed. Proposed guard specifications were being authored concurrently; their absence at initial read is not a finding.

## Ledger-wide assessment

The portfolio considers cheap mechanisms: one rung-1 obligation, five rung-3 obligations, one rung-4 obligation and two rung-5 obligations. Neither rung-5 obligation should move down: native linker/runtime behavior and the actual test-host command cannot be established by project flags. The ledger generally labels proposals honestly and leaves unexecuted native proof indeterminate. All nine current-tfm tracks fit net10.0 / C# 14 / SDK 10.0.303. The NuGet obligation is a CI capability rather than a TFM upgrade.

The principal mismatch is claim width: a symbol ban establishes absence of those symbols, but does not establish that a consumer-controlled provider supplies all relevant behavior. Three concerns follow.

## Findings

### PF1 — behavioral-time-provider: distinguish API prohibition from provider-controlled behavior

**Scope:** AutoscalingEngine, WorkerInfo/WorkerRegistry, DeadLetterHandler and RejectionRoutingOrchestrator.

**Concern:** Rung 3 is sound for rejecting the listed static APIs and direct Timer constructors. It is insufficient for the full claim that cooldowns, timers, ordering and FailedAt data accept a controllable clock. `TimeProvider.System.GetUtcNow()` or `TimeProvider.System.CreateTimer(...)` replaces the prohibited spelling while retaining ambient time. Preserving legacy constructors with system-clock fallbacks also means that merely adding a provider overload does not prove that real factories use it.

**Evidence:** AutoscalingEngine.cs:87 creates its own Timer; :192 reads UtcNow for cooldown. The evidence names factory wiring through AutoscalingExtensions, DeadLetterQueueExtensions and RejectionRoutingRegistration. `clock-through-di` only claims the WorkOrchestrator builder/priority latency path, so it does not establish the other composition paths. This is a boundary of the proposed mechanism, not a reason to remove the real source-backed debt.

**Suggested action:** Keep the recurring forbidden-API class at rung 3 with a correspondingly narrow claim. Add rung-4 evidence within this obligation, or extend the existing composition obligation, proving provider identity/effects at the affected public factories: fake-clock timer and cooldown progression, controlled worker creation times, and exact FailedAt values for both handling and rejected admission. The proposed monotonic cooldown change additionally needs an elapsed-time behavior assertion; a symbol ban cannot choose GetTimestamp/GetElapsedTime over wall-time subtraction. No production-host integration test is needed for these library component semantics.

### PF2 — deterministic-time-tests: an explicit System provider can bypass the intended distinction

**Scope:** AutoscalingEngineTests.AfterCooldown, AutoscalingEngineEventsTests and timer negative assertions.

**Concern:** G2's bare-delay ban is the cheapest sound guard for the bare-overload class, but the claim is wider: advancing modeled time rather than sleeping to establish a result. Replacing `Task.Delay(100)` with `Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System)` satisfies that proposed ban and still causes real time to make the cooldown assertion pass. The stated watchdog convention alone does not distinguish causal sleeps from legitimate deadlines.

**Evidence:** AutoscalingEngineTests.cs:290 constructs the engine without a clock; :296 sleeps between cooldown assertions. AutoscalingEngineEventsTests.cs:41,65,127,152,190,232 use real delays for events; AutoscalingEngineTimerTests.cs:182 uses one for a negative assertion. The scheduling policy expressly allows provider-taking delays, including System, so extending that policy alone leaves this semantic gap.

**Suggested action:** State that the basic API policy only closes bare-overload recurrence. For the broader claim, centralize system-clock watchdogs in a bounded approved helper and structurally restrict direct System clock access in these tests, or record the remaining semantic distinction as a reviewer-owned check. Retain focused fake-time component assertions demonstrating the causal advancement and a mutation/kill case that substitutes System. Do not use runtime-speed thresholds as the guard.

### PF3 — mtp-test-path: keep successful executable-host proof separate from the SDK runner and empty-selection proof

**Scope:** global.json, repository test commands and three test projects.

**Concern:** An MTP test executable launched by `dotnet run` proves that executable can discover and execute tests. It does not establish that `dotnet test` obeys global.json correctly, nor that an empty selection fails. A nonzero successful selection alone does not prove the negative contract.

**Evidence:** `architecture-tests.txt` exists and reports five passing tests under TUnit 1.2.11 / MTP 2.0.2 with the expected Scheduling test DLL path. `obligations/mtp-test-path.md` describes that run as `dotnet run`. At evaluation time `mtp-tests.txt` records a .NET CLI named-pipe bind Permission denied / FailFast, with SDK assembly version 10.0.303.0. That is infrastructure indeterminacy, not a product runner regression and not a passing SDK runner result. The draft ledger's indeterminate state is therefore appropriate.

**Suggested action:** Preserve distinct status/evidence for the locally successful executable-host selection, the SDK `dotnet test` invocation, and deliberate zero-match selection. Bind each run to command, configuration, revision, binaries and exit/result count. A later successful SDK retry can resolve that part; do not infer empty-selection rejection without an executed negative selection or verified existing host enforcement. Rung 5 remains appropriate; neither a new architecture check nor the global.json property alone substitutes for these executions.

## Per-obligation result

| Obligation | Assigned rung | Assessment |
|---|---:|---|
| behavioral-time-provider | 3 | Sound for API prohibition; full controllable-behavior claim exceeds proof. PF1. |
| clock-through-di | 4 | Cheapest sound rung for provider identity and observable latency through the public factory. BannedApi/type constraints do not establish resolved-instance reachability. Preserve proposed status until the test exists/runs. |
| deterministic-time-tests | 3 | Sound for bare-overload prohibition; explicit System escapes the broad semantic claim. PF2. |
| guard-subject-validation | 3 | Sound. Inspecting external PE metadata and expected assembly identity is structural analysis even when hosted in a test project. Its negative/self-tests validate the guard and do not make the protected claim rung 4. |
| short-lived-nuget-publishing | 3 | Sound for workflow credential provenance with a parser following the actual push token source and permissions. Remote trust activation remains a separately acknowledged rollout prerequisite; avoid claiming a live successful publish from static lint. |
| build-floor | 1 | Sound for the evaluated imported project policy. floor.json exists and reports effective project-level settings; build.txt exists and contains a successful Release solution build with zero warnings/errors. The driver exception is explicit. Do not turn preservation into a claim that later policy-file edits cannot disable the baseline. |
| scheduling-time-guard | 3 | Sound for its explicitly narrowed four clock properties and bare-delay overloads. Both projects reference the analyzer and same policy; effective warnings-as-errors is established. The metadata scanner's bad-subject behavior belongs to the separate obligation. |
| native-aot-path | 5 | Correct. Managed compilation/analyzers cannot establish native publish/link/run. Existing CI sample wiring is real, but remote action implementation and native execution are not proven by this evaluation. Indeterminate is honest. |
| mtp-test-path | 5 | Correct rung; distinguish execution scopes and negative contract. PF3. |

## Passes

- No above-floor feature is presented as current debt; no preview C# feature or TFM migration is required.
- Every obligation has a nonempty why-not-cheaper explanation. Clock composition and native execution explanations describe real structural limits rather than authoring preference.
- Existing BannedApi enforcement and shared MSBuild/CPM imports are reused. The proof set does not default to broad integration tests for syntactic policies.
- `floor.json`, `build.txt`, `architecture-tests.txt` and `mtp-tests.txt` exist; no missing artifact is represented here as executed proof. The architecture log proves five tests executed, rather than just test project compilation.
- Uninspectable PE subjects are correctly treated as non-passes, and testing the scanner itself is a necessary self-test rather than redundant product behavior testing.
- Proposed fixes/guards remain proposals. The source audit was not represented as completed remediation.

## Uncertainties

- The guard register and supplemental execution logs may change after this evaluation; synthesis should reconcile PF1/PF2 with the completed intended mechanisms and PF3 with any later bound run.
- No native sample was run by this evaluator; no remote CI action body, branch protection or NuGet account trust configuration was verified.
- This lens does not certify exhaustiveness of the eight-surface survey or functional correctness of unrelated algorithms.
