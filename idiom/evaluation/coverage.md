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

# Stage 3 — Coverage against the scope set

Fresh-context coverage examination of the ranked, eight-surface codebase baseline. No product changes. This review reads the survey, ledger, selected obligation evidence, project/sample graph, publication and CI workflows, scheduling guards, resilience policy generator, telemetry registration, and health registration. It does not re-run correctness tests or require exhaustive algorithm verification.

## Findings

### 1. native-aot-path — Preserve a partial disposition for the broad AOT claim

C2 (`README.md:9`) describes Bifrost as trim- and AOT-compatible without limiting the promise to scheduling and the concurrent queue. The active obligation deliberately narrows native execution to the two smoke samples. That is a sensible bounded proof target, but the claim corpus should say **partially covered**, even if those samples eventually pass. `Bifrost.Resilience.csproj:10–13` declares AOT compatibility and enables generated configuration binding; OpenTelemetry and HealthChecks also declare AOT compatibility. The two sample project references and their program bodies do not exercise those optional adapter compositions. The existing evidence file acknowledges analyzer evidence but does not explicitly map the unexercised public adapter paths to the residual C2 claim.

Suggested action: retain one native-path obligation; add a partial C2 disposition that names the sampled compositions and the remaining static-only adapter evidence. A small future native case for the generated HTTP configuration binder is a reasonable prioritized extension if stronger C2 coverage is desired. Do not demand a full cross-product of every public API and option, and do not claim adapter incompatibility from absent smoke coverage.

### 2. ledger-wide — Record the rank-7 logging candidate's applicability decision

Rank 7 includes resilience and telemetry, and the survey says the observability catalog was loaded. `ResiliencyPolicyGenerator.cs:267–271` constructs an interpolated retry message and passes it to `ILogger.Log`. Both retry callbacks call this helper (`:153` and `:193`). The loaded logging card specifically seeds interpolated logging but exempts rare diagnostic logs. The survey discusses generated binding, meters, factory seams, and health dictionaries without documenting whether retry logging is covered by that exemption.

Suggested action: make a targeted applicability/refutation decision for this concrete seed. If retry-only diagnostic frequency is insufficient to establish the catalog's hot-path premise, record that and retain the code; do not infer hot-path performance debt merely from the syntax. If the author has evidence of a hot retry path, the candidate belongs to rank 7 and changes the stopping result. No new obligation is established by this coverage observation alone.

### 3. ledger-wide — Distinguish the surface budget from the stopping predicate

The selected scope is clear and bounded: all eight ranked surfaces were surveyed, so the surface budget is satisfied. The ledger's preliminary disposition says ranks 7 and 8 produced “no new surviving class”; the skill's predicate says “no obligation that survives refutation.” Those can agree when cross-cutting obligations are merely reused, but they are not interchangeable without the final per-surface disposition. In particular, the rank-7 candidate above still needs a written applicability decision.

Suggested action: after synthesis, record each of ranks 7 and 8 as either no surviving candidate (with the principal rejected/reused mechanism) or containing a surviving candidate. If rank 7 adds an obligation, report **budget exhausted at rank 8; two-consecutive-empty stopping condition not reached**. No scope expansion or invented finding quota is required.

## Scope and claim accounting

| Rank | Coverage found | Assessment |
|---|---|---|
| 1 | build-floor, short-lived-nuget-publishing, mtp-test-path; SDK pin, imported props and package authority inspected | Meaningful cross-cutting coverage; remote action bodies and branch protection stay unknown. |
| 2 | behavioral-time-provider and clock-through-di | High-consequence behavioral time and composition receive separate evidence. |
| 3 | scheduling-time-guard, guard-subject-validation, native-aot-path | Both existing controls and their vacuous-subject failure mode are represented. |
| 4 | native-aot-path; specialized queue/layout rewrite candidates refuted | No unsupported performance modernization mandated. |
| 5 | clock-through-di; ScopedHandlerProxy refutation; public EnqueueResult default recorded as open design judgment | Public compatibility is preserved and unproven runtime debt is not promoted. |
| 6 | deterministic-time-tests, guard-subject-validation, mtp-test-path | Test-host discovery and deterministic time are distinct. |
| 7 | Shared build/AOT policies, generated binding and health-map refutations; logging seed noted above | No obligation quota; one applicability disposition remains useful. |
| 8 | Benchmark floor exception, documentation claim corpus and specialized optimization refutations | Appropriate limited investment in non-shipping workloads. |

C1/C5/C6 map to build-floor; C3 to mtp-test-path; C4 to scheduling-time-guard; C7 to the concrete async-scope implementation and refuted captive-scope allegation. C2 requires the partial-coverage qualification above. C4's deliberate system-clock barrier exceptions are already visible in the ledger and source (`ScheduleTickLoop.cs:238–241`, `:508–511`); no contradictory all-clocks-injected claim is needed.

Publication tests only `src/Bifrost.Tests/*.csproj` while CI selects `src/Bifrost.Tests*/*.csproj`. The survey already identifies this as a release-coverage question. This is not proof of a broken MTP host and does not need a new idiom violation absent an established release-gate requirement. Preserve that limitation rather than suggesting an all-project release run has been established.

## Passes

- Ranking, consequence/exposure rule, eight-surface budget, unanswered scope assumption, and concrete SDK/language floor are recorded.
- The ledger spans rungs 1, 3, 4, and 5; it is not confined to rewrites or cosmetic language changes.
- MSBuild, global.json, imported analyzer settings, package management, public constructors and rollback mechanisms are included.
- High-risk shipping time and guard paths receive greater attention than benchmarks and cosmetic modernization.
- Known detector errors (EF and pre-restore warning settings) are corrected against source/import evidence.
- No serializer/TFM transition is proposed, so no omitted serializer rollout/rollback matrix is implied.
- Missing optional-domain findings are not themselves gaps: there is no requirement to promote one catalog card per domain.

## Uncertainties

Remote workflow implementation and NuGet trust state remain outside established evidence. This lens does not turn pending build, test, architecture, or native runs into green evidence. Guard/specification and manifest work in progress were intentionally not evaluated as missing deliverables. Optional adapter AOT execution and retry-log hotness remain unestablished, not failed.
