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

# Guard register

All new mechanisms below are **specified proposals**, not installed guards. Classes remain open today. Existing scheduling compiler rules and architecture checks remain intact. Owners below are accountable roles, not claims that someone accepted an assignment. Default exception expiry: none permitted until owner and explicit date are recorded. No policy uses a new custom file format where BannedSymbols/MSBuild already suffice.

## G1 — behavioral-time-provider (production)

- **Class / recurrence:** direct behavioral clocks; AutoscalingEngine.cs:192 and WorkerInfo.cs:41, plus DLQ data in DeadLetterHandler.cs:95 and RejectionRoutingOrchestrator.cs:271. At least four types / four behavioral clock reads and one timer construction.
- **Earliest sound layer:** rung 3, BannedApi symbol analysis. Replacement constructors still need to exist; DI identity is independently covered by clock-through-di.
- **Policy data:** proposals/BannedSymbols.Time.txt, extending skill scripts/templates/BannedSymbols.sample.txt and the existing scheduling eight-entry policy. proposals/Directory.Build.props.snippet extends the skill props snippet and reuses central analyzer version 4.14.0. Proposed destination src/IdiomPolicy; link into Bifrost as AdditionalFiles. Merge applicable missing entries into the scheduling authority rather than maintain divergent copies long term.
- **Mechanism:** resolve forbidden symbols during Release build with RS0030 error. The System.Threading.Timer type ban covers every constructor overload; store the returned ITimer. Do not ban TimeProvider-taking Delay/PeriodicTimer/CTS overloads. Prefer provider.GetTimestamp/GetElapsedTime for elapsed cooldown; provider.GetUtcNow for data timestamps.
- **Kill fixtures:** copy exact UtcNow expression from AutoscalingEngine:192 and Timer constructor from :87 into a scratch project importing the same package/policy. Existing application instances must diagnose before remediation.
- **Self-test:** compile one probe per policy symbol; require expected RS0030 on the marked source span and nonzero exit. Compile compliant TimeProvider counterparts successfully. Remove the AdditionalFiles item and separately the analyzer reference: the wrapper must report an unexpected successful bad build and fail. Deliberately break restore: wrapper must emit indeterminate/nonzero, never count any build failure as a successful kill.
- **Protected path:** existing CI build-test and publish Release build. Add a negative-compiler-probe step to build-test before ordinary tests; do not infer remote branch protection from local YAML.
- **Signals:** pass = matched SDK/TFM/policy hash + clean production compile + all bad probes diagnosed as RS0030 + compliant probe compiled. Fail = forbidden symbol. Indeterminate = compiler unavailable, mismatched subject, missing expected diagnostic or timeout; exit nonzero.
- **Limits:** each tiny build ≤60s; cancel whole probe set at 5 minutes; no network dependency after restore.
- **Exceptions:** event-only timestamp sites in EventStreamOrchestrator and WorkerRegistry may be observational; if retained, scoped RS0030 suppression adjacent to the method must name library maintainer role, reason and expiry. No blanket project NoWarn. Compatibility overloads may select TimeProvider.System; this is an explicit default, not a banned timestamp call.
- **Owner / expiry:** library maintainers; no permanent exception approved by this report.
- **Status:** specified, unexecuted as a new production guard; class open.

## G2 — deterministic-time-tests

- **Class / recurrence:** elapsed-time sleeps in AutoscalingEngineTests:296 and AutoscalingEngineEventsTests:41,65,127,152,190,232; negative-event sleep in AutoscalingEngineTimerTests:182.
- **Earliest layer / policy:** rung 3; proposals/BannedSymbols.TestTime.txt and props snippet, installed on Bifrost.Tests only. Extend existing scheduling Delay symbols and skill Thread.Sleep template. No broad ban on asynchronous waiting.
- **Mechanism:** RS0030 prohibits bare Task.Delay and Thread.Sleep in this project. Use FakeTimeProvider for time progression, barriers for concurrent progress, explicit TimeProvider.System for watchdogs. An explicit provider is necessary but not sufficient to prove all tests deterministic: reviewers own correct distinction between test subject time and harness deadline.
- **Kill fixture:** existing `await Task.Delay(100)` from AutoscalingEngineTests:296, and `Thread.Sleep(1)` negative probe for template entry.
- **Self-test:** same positive/negative compiler protocol as G1; removing analyzer/policy must cause probe runner failure. Retain public cooldown/event assertions with fake time; replacing Advance with a system delay must fail either compiler policy or deterministic assertion.
- **Protected path / signals / limits:** Bifrost.Tests build in build-test; RS0030 failure; missing analyzer/no tests/timeout indeterminate with nonzero gate; 60s per compiler probe, 10s per fake-time component probe.
- **Exceptions:** bounded real-clock integration deadline uses explicit TimeProvider.System and a reason comment. Any actual RS0030 suppression requires testing maintainer owner and date, no blanket NoWarn.
- **Owner / expiry:** testing maintainers; none approved.
- **Status:** specified, class open; successful existing real-time tests do not refute the structural finding.

## G3 — guard-subject-validation

- **Class / recurrence:** silent no-metadata skips in BannedTimeApiTests:143,208 and AotSafetyTests:164.
- **Earliest layer:** rung 3; extend existing metadata scanners rather than add another scanner framework.
- **Policy data:** the expected assembly paths already listed by SchedulingAssemblyPaths (both Scheduling.Core and Scheduling). Extend to expected simple assembly names; derive banned member matching from the existing symbol policy where feasible. Keep expected subjects separate from scan implementation. A well-formed but wrong managed DLL is also indeterminate, not clean.
- **Mechanism:** reject zero subjects, missing subjects, non-metadata PE, unexpected assembly names, unreadable/malformed metadata and signature decode failures. Record inspected count and matching SHA256 of each input. Return violation vs invalid-subject distinctly; both block CI. Compare actual TimeProvider parameter type, not only arity, when extending delay checks.
- **Kill fixture:** copy a built Scheduling.Core DLL into /tmp; clear the PE CLI-header directory to produce a valid PE with no CLR metadata. Existing code reaches continue and yields zero violations. Do not overwrite normal outputs. Existing UtcNow fixture remains the positive detection control.
- **Self-test:** native PE, wrong managed DLL, missing file, empty subject list, deliberate forbidden UtcNow/Delay and compliant DLL cases. Require all invalid subjects to be rejected and every expected bad symbol detected. Removing either the subject check or violation check must make the respective probe fail; malformed input exception is an infrastructure failure, not evidence of a caught banned API.
- **Protected path:** Architecture category through existing build-test test-project pattern. Add self-tests in existing architecture test classes.
- **Signals:** pass = exact expected managed subjects inspected and no violations; fail = forbidden reference; indeterminate = invalid/unreadable/wrong subject. Both latter outcomes produce failing test exit. No catch-and-return-empty path.
- **Limits:** ≤10s for two small assemblies and fixture set; bounded file size ≤100MB; no remote input.
- **Exceptions / owner / expiry:** no subject exception; testing maintainers; none approved.
- **Status:** source-confirmed gap, native-PE fixture executed against all three existing scanner methods; each returned zero violations. Other proposed self-tests remain unexecuted; existing positive architecture suite does not establish bad-subject handling.

## G4 — short-lived-nuget-publishing

- **Class / recurrence:** two stored-secret push loops, publish.yml:56 and :65.
- **Earliest layer:** rung 3 workflow structural validation. Upgrade is CI-only, with no library TFM bump.
- **Policy data:** proposed policy in publish.yml requires id-token write in the publish job, a pinned NuGet/login step id, temporary login output used by both pushes, and the expected nuget.org source. The current workflow lacks that login flow. Check parsed workflow nodes rather than merely searching the word NUGET_API_KEY: temporary login output legitimately has that name.
- **Mechanism:** require every nuget.org push credential to originate from the designated login output; reject secrets.NUGET_API_KEY or unresolved aliases. Allow private feed rules only with feed-specific policy. Remote nuget.org trust binding must be separately configured by owner before enabling the workflow.
- **Kill fixture:** current package and symbol push steps, as two fixtures. The second step's continue-on-error does not waive credential policy.
- **Self-test:** current stored-secret workflow must fail; compliant workflow must pass; missing publisher job, parser exception, unresolved environment indirection and omitted guard execution must all yield nonzero/indeterminate. Remove the check from CI and workflow validation must detect missing required step.
- **Protected path:** proposed build-test workflow-lint step; blocks accepting publication workflow changes. Actual remote branch protection not verified.
- **Signals:** pass = both credential flows and workflow identity verified structurally; fail = stored credential origin; indeterminate = unparseable/unresolved/empty workflow or remote login unavailable. No secret content needed.
- **Limits:** local workflow parse ≤10s, no network. OIDC login only during authorized release workflow, not this audit.
- **Exceptions / owner / expiry:** package maintainers / NuGet account owner; none approved. Private-feed exception must identify exact source and expiry.
- **Status:** specified; remote policy configuration needs owner input at implementation time. Class open.

## Existing scheduling guard

The existing eight-symbol scheduling BannedApi policy and five Architecture tests were inspected. Preserve BannedApiViolationFixture.GetNow as a negative subject. A successful current-revision five-test run verifies the declared existing subset, while G3 tracks scanner subject integrity. Proposed TimePolicy additions are not retroactive defects in the narrower DR-7 rule.

## Composition behavior proof

clock-through-di includes the confirmed explicit constructor omission plus replacement behavior in autoscaling, worker ordering and persisted DLQ timestamps. It is assigned public-registration component proofs rather than a repository-wide constructor analyzer. Kill edits: omit the builder provider argument; substitute System in each behavioral class; disable provider timer creation. Self-test must observe the provider through timestamp-dependent behavior; service resolution or field reflection alone does not establish effect. Failure/no test execution must not pass. This is a structural composition change and the gap remains open.
