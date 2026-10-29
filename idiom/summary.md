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

# Summary

## Floor

At `3a17ed0f45dfd64d953712ccda286b0915bef838`, shipping and sample projects target .NET 10 with C# `latest` (14 on the actual SDK 10.0.303), nullable/implicit usings, analyzers/latest analysis, warnings-as-errors and CPM. TUnit 1.2.11 runs on MTP 2.0.2. `detect-floor.py` evaluated MSBuild before and after restore; imported `Lvlup.Build` resolves the apparent pre-restore warning-policy gap. The isolated benchmark driver has no direct packages and does not inherit the shared policy.

## Classes a single guard would close

- **Implicit test clocks:** repeated real-time waits in autoscaling tests. G2 proposes the existing scheduling Delay policy plus the skill Thread.Sleep template, with explicit watchdog handling. A passing suite does not eliminate timing dependence.
- **Behavioral system clocks:** cooldown, timer creation, worker-age ordering and persisted DLQ timestamps. G1 extends the existing BannedApi policy using the skill templates; G2 reuses that mechanism for tests. Observational event timestamps were excluded from the finding.
- **Guard input acceptance:** three scanner branches treat a native PE as zero violations. A scratch probe reproduced all three. G3 specifies subject validation and negative-input self-tests.
- **Stored publishing credentials:** both NuGet push loops use a stored secret. G4 specifies a trusted-publishing credential-flow check; package-owner trust configuration is a rollout prerequisite.

These are **structural proposals**, with kill fixtures and self-tests in [guards.md](guards.md); none is installed by this audit.

## First structural move

Extend the existing time policy to the behavioral orchestration paths and wire one consumer-selected `TimeProvider` through construction. `WorkOrchestratorBuilder` currently omits the available clock parameter. Preserve published constructors with delegating overloads. Separately prove the selected clock controls enqueue latency, autoscaling cooldown/timers, worker ordering and DLQ FailedAt through public registrations. An API ban alone cannot prove that behavior.

Validation: Release solution build passed with **0 warnings / 0 errors**; **5 scheduling Architecture tests** and **22 focused autoscaling tests** passed. The SDK MTP runner rejected a deliberately empty selection with exit **8**. Successful existing tests do not refute the five source-backed violations.

## Indeterminate

The baseline contains **9 obligations: 5 violated, 3 locally verified, 1 indeterminate**. Native AOT publish/run was not performed; the README-wide optional adapter compositions exceed the two inspected samples. Remote shared CI implementation, branch protection and NuGet trust configuration were not established. Scope question was unanswered; repository-wide scope was assumed. Injected lvlup.md and USER-CONTEXT.md were missing. The audit stopped at its eight-surface budget; uninspected methods and algorithm/performance correctness are not certified. Full detail and exact revision bindings are in [ledger.md](ledger.md) and evidence/bindings.json.
