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

# Idiom obligation ledger

Source of truth for this baseline. Scope and lens records: survey.md. Guard specifications are proposals; no product remediation applied.

## Active obligations

### behavioral-time-provider — Control behavioral time through TimeProvider

| | |
|---|---|
| **Claim** | The named behavioral paths must reject direct DateTimeOffset.UtcNow and System.Threading.Timer construction; replacement clock behavior is a separate component obligation. |
| **Scope** | Bifrost AutoscalingEngine, WorkerInfo/WorkerRegistry, DeadLetterHandler and RejectionRoutingOrchestrator. |
| **Consequence** | Consumers cannot control cooldowns or worker-age ordering deterministically; tests sleep and DLQ timestamps cannot be set from the same host clock. |
| **Legacy shape** | DateTimeOffset.UtcNow and new System.Threading.Timer on behavioral paths. |
| **Modern replacement** | Inject TimeProvider; use CreateTimer/ITimer, GetUtcNow and monotonic timestamps for elapsed cooldowns. Preserve old public constructor signatures as delegating overloads. |
| **Catalog id** | time-via-timeprovider |
| **Track** | current-tfm |
| **Min TFM** | net8.0 |
| **Proof rung** | 3 — deterministic structural analysis |
| **Proof artifact** | Proposed shared BannedApi time policy in idiom/proposals; G1 rejects the listed symbols. clock-through-di separately establishes selected-clock effects. |
| **Why not cheaper** | SDK generation/types cannot reject direct BCL calls. Rung 3 establishes only the symbol exclusion; the identity and behavior of the replacement provider require rung 4. |
| **Failure signal** | Currently no clock ban in Bifrost. Proposed RS0030 build error; restore/analyzer failure or missing fixture diagnostic is indeterminate, never clean. |
| **Rollback** | Revert optional overloads and policy together; preserve published constructors. Clock-source behavior changes may change timestamps; do not rewrite historical DLQ records. |
| **State** | violated |
| **Recommendation** | structural |
| **Lenses** | cosmetic-modernity, promise-against-delivery, recurrence-to-guard |

**Open questions:**

- None

### clock-through-di — Honor the consumer clock through the public builder

| | |
|---|---|
| **Claim** | The proposed clock-injection contract must honor a consumer clock through the public builder and through autoscaling timers/cooldown, worker creation ordering, and persisted DLQ FailedAt values. |
| **Scope** | WorkOrchestratorBuilder plus WithAutoscaling/worker registry and WithDeadLetterQueue/rejection registration compositions; timestamp-dependent behavior. |
| **Consequence** | Custom/fake clocks cannot control the listed behavioral paths; direct-constructor tests and symbol bans alone miss composition and replacement-semantic mistakes. |
| **Legacy shape** | Optional clock dependency silently defaulting to TimeProvider.System in an explicit new call. |
| **Modern replacement** | Preserve caller TimeProvider registrations using TryAddSingleton(System); pass the selected provider through constructors/factories. Preserve public signatures using delegating overloads. |
| **Catalog id** | uncatalogued |
| **Track** | current-tfm |
| **Min TFM** | net8.0 |
| **Proof rung** | 4 — contract/component tests |
| **Proof artifact** | Proposed public-registration component tests: counting provider for enqueue/latency; fake timer and cooldown boundary; fake CreatedAt and registry order; exact fake FailedAt for handler failure and admission rejection. No private-field reflection. |
| **Why not cheaper** | Properties and non-null types do not select the correct DI instance. Banned symbols cannot distinguish injected versus system providers; a component test through the factory establishes the identity-dependent effect. |
| **Failure signal** | Current source omits the optional WorkOrchestrator provider and other behavioral classes use static time. Proposed component tests must fail when each path substitutes System; missing selection/timeout is indeterminate. |
| **Rollback** | Revert the registration/argument/test together; retain existing public API and fallback for direct construction. |
| **State** | violated |
| **Recommendation** | structural |
| **Lenses** | integration-completeness, authority-topology |

**Open questions:**

- None

### deterministic-time-tests — Use fake time for elapsed-time assertions

| | |
|---|---|
| **Claim** | Autoscaling unit tests must reject bare Task.Delay and Thread.Sleep and make clock selection explicit; fake-time behavior is tested separately under clock-through-di. |
| **Scope** | AutoscalingEngineTests.AfterCooldown and AutoscalingEngineEventsTests; timer-test negative assertions. |
| **Consequence** | Wall-clock timing makes tests slower and load-sensitive, and hides inability to inject production timers. |
| **Legacy shape** | Bare Task.Delay used as causal progress or as proof that no future event occurs. |
| **Modern replacement** | FakeTimeProvider.Advance plus event/barrier synchronization. Explicit TimeProvider.System watchdogs are allowed for test-harness deadlines. |
| **Catalog id** | timeprovider-in-tests |
| **Track** | current-tfm |
| **Min TFM** | net8.0 |
| **Proof rung** | 3 — deterministic structural analysis |
| **Proof artifact** | Proposed G2: scheduling bare-delay BannedSymbols entries applied to Bifrost.Tests, scoped diagnostic policy and explicit watchdog conventions; component tests retain behavior assertions. |
| **Why not cheaper** | SDK/types do not prohibit these static overloads. Symbol analysis proves the narrow exclusion only; review of system-clock watchdog exceptions and component tests own causal-time semantics. |
| **Failure signal** | Existing suite may pass despite these sleeps. Proposed RS0030 on bare delay; expected negative fixture diagnostic required; timeout/no tests is indeterminate. |
| **Rollback** | Revert fake-time test changes with production time injection if necessary; keep explicit deadline watchdogs so stalled tests fail. |
| **State** | violated |
| **Recommendation** | structural |
| **Lenses** | cosmetic-modernity, recurrence-to-guard |

**Open questions:**

- None

### guard-subject-validation — Reject uninspectable guard subjects

| | |
|---|---|
| **Claim** | Architecture scanners must reject a subject without CLR metadata, and must establish that every expected managed assembly was inspected. |
| **Scope** | BannedTimeApiTests.FindMemberReferenceViolations / FindNonTimeProviderTaskDelayViolations and AotSafetyTests scanner. |
| **Consequence** | A valid native PE substituted for a subject yields an empty violations list and can be interpreted as idiom success without inspecting managed code. |
| **Legacy shape** | if (!peReader.HasMetadata) continue in a guard that returns only violations. |
| **Modern replacement** | Fail on unexpected/non-managed subjects; validate expected assembly identity and nonzero subject count; share policy and scanner tests. |
| **Catalog id** | no-vacuous-tests |
| **Track** | current-tfm |
| **Min TFM** | net6.0 |
| **Proof rung** | 3 — deterministic structural analysis |
| **Proof artifact** | Proposed G3: existing architecture scanners with subject validation and kill/self-test cases, without replacing compiler BannedApi enforcement. |
| **Why not cheaper** | SDK/types cannot prove that an external PE file is the intended built assembly. Structural metadata inspection is the earliest sound check for this specific subject-binding claim. |
| **Failure signal** | Currently !HasMetadata can return no violations. Proposed malformed/wrong/native subject yields an explicit error/indeterminate check result and nonzero gate, never a pass. |
| **Rollback** | Revert scanner validation only with an explicit owner review; no product behavior changes. Existing compiler analyzers remain in place. |
| **State** | violated |
| **Recommendation** | structural |
| **Lenses** | existing-proof-inventory, recurrence-to-guard, wildcard |

**Open questions:**

- None

### short-lived-nuget-publishing — Use trusted NuGet publishing credentials

| | |
|---|---|
| **Claim** | Nuget.org package and symbol publishing should use a short-lived trusted-publishing token. |
| **Scope** | .github/workflows/publish.yml package and symbol push steps. |
| **Consequence** | Publishing relies on a stored API-key secret and its rotation rather than workflow-bound identity. |
| **Legacy shape** | Both dotnet nuget push loops consume secrets.NUGET_API_KEY. |
| **Modern replacement** | NuGet trusted-publishing policy bound to repository/workflow/environment, id-token: write and SHA-pinned NuGet/login; pass its temporary output token. |
| **Catalog id** | nuget-trusted-publishing |
| **Track** | current-tfm |
| **Min TFM** | N/A — CI capability; no TFM upgrade |
| **Proof rung** | 3 — deterministic structural analysis |
| **Proof artifact** | Proposed G4 workflow check parses push credentials and permissions; NuGet account configuration must be supplied by package owner. |
| **Why not cheaper** | Project generation/compiler cannot govern GitHub workflow credentials. Workflow structural validation is sufficient for credential provenance; it cannot verify remote trust activation. |
| **Failure signal** | Currently no guard identified. Proposed stored-secret push fails lint; parser/no workflow/missing token source is indeterminate; actual login failure blocks publishing. |
| **Rollback** | Revert workflow with package owner-managed credential rotation. Remote trust policy removal is separate; no secret was read or changed by this audit. |
| **State** | violated |
| **Recommendation** | structural |
| **Lenses** | upgrade-and-compatibility, recurrence-to-guard |

**Open questions:**

- Which NuGet account owner will create the workflow trust policy? (needs human input; repository workflow inspected, account policy is external.)

### build-floor — Preserve the evaluated modern build floor

| | |
|---|---|
| **Claim** | Shipping and sample projects use net10.0, nullable analysis, central package versions and build-time warnings-as-errors. |
| **Scope** | src and samples project graph; benchmark driver explicitly outside shared policy. |
| **Consequence** | An import loss silently weakens the common compiler/analyzer baseline. |
| **Legacy shape** | Per-project dependency versions, nullable/analyzer disable switches, warning-only builds. |
| **Modern replacement** | Existing SDK-style projects, shared Directory.Build.props / Directory.Packages.props and pinned Lvlup.Build imports. |
| **Catalog id** | implicit-usings-and-build-props |
| **Track** | current-tfm |
| **Min TFM** | net6.0; net8+ SDK for CPM catalog convention |
| **Proof rung** | 1 — construction and generation |
| **Proof artifact** | idiom/floor.json after restore; idiom/build.txt; shared props and Lvlup.Build 1.4.0. |
| **Why not cheaper** | Rung 1 is cheapest: SDK/MSBuild constructs the common floor; no replacement test framework needed. |
| **Failure signal** | Build failed on missing assets before restore, then succeeded with zero warnings/errors after restore; evaluation is bound to HEAD and SDK 10.0.303. Missing imports remain indeterminate, not false compliance. |
| **Rollback** | Restore pinned imports/central versions; reverting nullable or package constraints alters the baseline and requires explicit review. |
| **State** | verified |
| **Recommendation** | structural |
| **Lenses** | mechanism, promise-against-delivery, claim-derivation |

**Open questions:**

- None

### scheduling-time-guard — Keep scheduling clock APIs structurally guarded

| | |
|---|---|
| **Claim** | Shipping Scheduling and Scheduling.Core code must avoid the four declared static clock properties and bare Task.Delay overloads. |
| **Scope** | Scheduling/Core BannedSymbols AdditionalFiles and scheduling Architecture tests. |
| **Consequence** | A recurrence of bare system time defeats fake-time scheduling. |
| **Legacy shape** | DateTime/DateTimeOffset Now/UtcNow; Task.Delay overload without TimeProvider. |
| **Modern replacement** | Existing shared BannedSymbols and TimeProvider-taking delays; explicit system-clock barrier exceptions documented at ScheduleTickLoop:238 and :508. |
| **Catalog id** | time-via-timeprovider |
| **Track** | current-tfm |
| **Min TFM** | net8.0 |
| **Proof rung** | 3 — deterministic structural analysis |
| **Proof artifact** | Existing BannedSymbols.txt, BannedApiAnalyzers 4.14.0 and BannedTimeApiTests; bound architecture run recorded in architecture-tests.txt. |
| **Why not cheaper** | Clock abstraction alone cannot forbid BCL static APIs. Symbol analysis is the earliest restriction on these calls. |
| **Failure signal** | Restored Release build passed with warnings-as-errors and all five Architecture tests passed at HEAD. Existing UtcNow kill fixture detected. Scanner subject integrity is separately violated; no broader clock completeness claimed. |
| **Rollback** | Revert rule changes only with documented exception owner/expiry; keep fake-time behavior tests. |
| **State** | verified |
| **Recommendation** | structural |
| **Lenses** | promise-against-delivery, claim-derivation, recurrence-to-guard |

**Open questions:**

- None

### native-aot-path — Validate the claimed native shipping path

| | |
|---|---|
| **Claim** | The advertised AOT compatibility must hold for the public scheduling and concurrent-queue compositions exercised by smoke samples. |
| **Scope** | Inspected two sample compositions only. README-wide optional Resilience/HealthChecks/OpenTelemetry native compositions remain uninspected and indeterminate. |
| **Consequence** | A managed build alone can miss native-link/runtime composition failures seen by consumers. |
| **Legacy shape** | Claiming AOT compatibility solely from analyzer flags or a managed build. |
| **Modern replacement** | Existing trim/AOT analyzers plus native publish and execution of the real smoke samples. |
| **Catalog id** | aot-trim-analyzers |
| **Track** | current-tfm |
| **Min TFM** | net8.0 |
| **Proof rung** | 5 — production-path integration tests |
| **Proof artifact** | .github/workflows/ci.yml:65–78 aot-smoke actions and samples; local native execution not yet established. |
| **Why not cheaper** | Rungs 1–3 establish static compatibility only; component tests under the CLR do not run the native linker/runtime. The claim is deliberately native composition, not just analyzer enablement. |
| **Failure signal** | Native publish warnings/link failures or sample nonzero exits fail; absent toolchain/no execution is indeterminate, not green. |
| **Rollback** | Revert incompatible APIs while retaining existing net10.0 packages; do not drop AOT claims without a release compatibility decision. |
| **State** | indeterminate |
| **Recommendation** | structural |
| **Lenses** | promise-against-delivery, claim-derivation, upgrade-and-compatibility |

**Open questions:**

- Native smoke execution at this revision remains unestablished (partial: sample/CI wiring and managed build inspected; native publish/run not performed).

### mtp-test-path — Retain the correct MTP test path

| | |
|---|---|
| **Claim** | The repository test command must execute TUnit tests under the configured MTP host and reject empty selections. |
| **Scope** | Local SDK dotnet test path for the focused Bifrost.Tests selection; other project/CI runs remain outside this verified claim. |
| **Consequence** | Incorrect runner/filters can produce misleading green test evidence. |
| **Legacy shape** | VSTest-only arguments or unbound/empty MTP runs. |
| **Modern replacement** | Existing MTP global.json runner, TUnit 1.2.11, documented --solution/--project and CI per-project execution. |
| **Catalog id** | microsoft-testing-platform |
| **Track** | current-tfm |
| **Min TFM** | SDK 10.0+ |
| **Proof rung** | 5 — production-path integration tests |
| **Proof artifact** | mtp-tests.txt: SDK dotnet test, 22 passed. mtp-empty-selection.txt: zero tests rejected with exit 8. architecture-tests.txt: separate five-test MTP executable run. No remote run inferred. |
| **Why not cheaper** | Properties and structural flags cannot prove test discovery/execution; running the actual host with a nonzero test count establishes the path. |
| **Failure signal** | 22 tests passed through SDK MTP command; deliberate empty class filter returned exit 8. Initial sandbox named-pipe denial was infrastructure-indeterminate and resolved by an authorized local rerun. |
| **Rollback** | Revert runner/argument changes together; do not mix VSTest-only switches with this host. |
| **State** | verified |
| **Recommendation** | structural |
| **Lenses** | promise-against-delivery, claim-derivation |

**Open questions:**

- None

## Refuted

- **Warnings-as-errors missing** — refuted by post-restore floor evaluation and Lvlup.Build imports; initial no-assets evaluation omitted package props.
- **CPM drift from detector cpm=false** — driver has no direct PackageReference; src/samples share central versions. No conflicting version authorities found.
- **EF migration debt** — detector matched comments naming consumers' DbContext, not an EF implementation.
- **README wrong MTP command** — README:152 already says `dotnet test --solution src/Bifrost.sln`; CLAUDE.md warning is stale.
- **Replace every clock timestamp** — event-only telemetry timestamps fall under the observational when-not clause; only behavior/order/DLQ data promoted.
- **Convert options to immutable records / use C# 15 unions** — binding mutability is intentional; preview is above this floor.
- **ThreadStatic is ambient request state** — ThreadHandle owns per-thread randomized sampling; it does not carry async request identity.
- **Freeze every dictionary / remove custom concurrency layout** — health dictionaries are created per check and policy dictionary is mutable configuration; no measured evidence establishes a superior substitute for specialized queue layout.
- **Captive handler scope** — ScopedHandlerProxy:58–60 uses await using/CreateAsyncScope and forwards cancellation. Claim C7 has a concrete implementation; native/exhaustive lifetime behavior is not claimed verified here.
- **Hot-path LoggerMessage mandate for retry logging** — ResiliencyPolicyGenerator:267–271 interpolates logs, but execution is an exception/retry path and no workload evidence establishes a hot-path cost. Keep as situational diagnostic improvement; do not impose CA1848 repository-wide from this evidence.
- **EnqueueResult default is a proven runtime failure** — default struct is false/null, but no shipped producer returning default found in the surveyed call sites. Keep API design concern below as unproven impact, not a runtime violation.

## Open questions and limits

- Scope answer was not received. The assumed whole-repository scope is not a verbatim author answer.
- Missing shared lvlup.md and USER-CONTEXT.md may contain context unavailable to this run.
- Remote CI action bodies/branch protection and NuGet account trust settings are not established; no remote green run claimed.
- EnqueueResult default semantics warrant API-owner judgment before any public shape change. Existing factories do not prove all external consumers use them; no breaking change is prescribed.
- Catalog preview entries are upgrade leads, never current violations. No .NET 11 migration proposed.
- Guard specifications/negative fixture procedures still need implementation where marked proposed. The existing scanner was experimentally shown to accept a native PE as zero violations (scanner-probe.txt). No proposed guard closes its class today.

## Disposition and stopping position

Fresh-context evaluation completed (proof-layer-fit, coverage, refutation, wildcard). Stopped at rank 8 by the declared surface budget, not by claiming the two-zero-surfaces condition was established. Cross-cutting AOT obligations touch rank 7; rank 8 adds no surviving obligation. The eight-surface bound is exhausted; no ranked surface was silently omitted. Uninspected methods/algorithms remain indeterminate; this is not exhaustive correctness or performance verification.
