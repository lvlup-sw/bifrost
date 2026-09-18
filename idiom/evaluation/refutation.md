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

# Refutation lens

Fresh-context Stage 3 pass. Scope question remains unanswered; whole repository is the recorded assumption. No additional external sources fetched. This pass read all nine obligation evidence files and discriminating source paths. Behavioral time and DI composition received individual attempts; tests/guard integrity and retained build/runner/native claims received mechanism-level treatment. A surviving obligation is not automatically a verified claim or an implemented guard.

## Verdicts

| Slug | Verdict | Refutation attempt and discriminating evidence |
|---|---|---|
| behavioral-time-provider | survived | Observational-only timestamps would defeat applicability, but AutoscalingEngine.cs:192–195 subtracts ambient UTC to decide cooldown, :87 constructs the timer, and WorkerRegistry.cs:138–142 uses WorkerInfo.CreatedAt to select workers to stop. These change behavior. AutoscalingExtensions.cs:73 registers the engine, so this is reachable production code. DeadLetterHandler.cs:91–99 and RejectionRoutingOrchestrator.cs:267–276 put timestamps into public queue records. Narrow the event-only examples as explained below. |
| clock-through-di | survived | An optional constructor parameter or separate scheduler registration does not supply an explicit `new`. DependencyInjection/WorkOrchestratorBuilder.cs:148–152 omits timeProvider; WorkOrchestrator.cs:114,:123 falls back to System. :271,:324 stamp envelopes; :144,:146 configure priority timestamp frequency; :610 observes elapsed queue time. SchedulerServiceCollectionExtensions.cs:104 registers TimeProvider but cannot alter that explicit constructor call. This is a composition improvement, not proof that README already promises this contract. |
| deterministic-time-tests | survived | Some real delays are legitimate watchdogs, but AutoscalingEngineTests.cs:281–302 explicitly sleeps 100ms to expire a 50ms cooldown, AutoscalingEngineEventsTests.cs:29–45 waits 200ms for timer progress, and AutoscalingEngineTimerTests.cs:159–185 waits 300ms as negative evidence after stop. These are modeled component behavior, not external-clock integration tests. Production injection alone will not stop these tests sleeping, so this is distinct from behavioral-time-provider. |
| guard-subject-validation | survived | A missing subject already throws, and the current path list is nonempty; neither refutes the narrower nonmanaged-subject branch. BannedTimeApiTests.cs:30–34 constructs disk filenames rather than obtaining each subject from its loaded Assembly, and :143–146/:208–211 skip valid PE files lacking CLR metadata. AotSafetyTests.cs:32–36,:164–167 repeats it. Callers assert only an empty violation list (:59,:85 and :62 respectively). Existing compiler bans protect source compilation but do not establish scanner subject identity. This is a guard-integrity defect, not evidence current CI actually scans native files. |
| short-lived-nuget-publishing | survived | The private-feed exception does not apply: publish.yml:56–57 and :65–66 pass a GitHub stored secret to api.nuget.org. It is not a short-lived login output merely named API_KEY. The repository proves credential provenance; it does not prove key age, permissions, leakage, or remote trust setup. Correct stale line references and narrow the symbol-step failure claim below. |
| build-floor | survived | Existing controls refute a missing-modern-build defect, but this obligation explicitly preserves a verified baseline. src/Directory.Build.props:4–8 establishes target/language/nullability; samples/Directory.Build.props imports it; both central-version authorities converge on src. Restored floor.json and build.txt support effective imports. Keep as retained control with no proposed product change. Do not count it as remediation debt. |
| scheduling-time-guard | survived | Existing control is the subject of this baseline claim, not evidence of missing enforcement. Scheduling/Core project files reference BannedApiAnalyzers and the same Core/BannedSymbols.txt containing the eight declared symbols. BannedTimeApiTests:73–78 explicitly permits system-clock barrier overloads, which therefore do not refute the narrow banned-symbol claim. architecture-tests.txt records 5 successful tests under MTP, including the deliberate-violation test. A claim that all possible ambient time APIs are forbidden would be broader and unestablished. |
| native-aot-path | survived | README.md:9 advertises AOT compatibility, and .github/workflows/ci.yml:63–77 wires two native smoke paths. This defeats a claim that no native gate exists. It does not establish that the remote action ran successfully at this revision, nor that the audit executed native artifacts. Retain as an existing proof obligation with state indeterminate, not as a discovered use of incompatible APIs. |
| mtp-test-path | survived | global.json explicitly chooses MTP and README.md:152–155 already gives the correct command. Those facts defeat a migration/incorrect-README defect. They do not prove host execution and selection. architecture-tests.txt demonstrates a local scheduling test executable run with 5 tests; mtp-tests.txt contains a dotnet-test host failure. Keep claims tied to the command actually executed; broader advertised SDK test-path success remains indeterminate. |

## Actionable concerns

1. **behavioral-time-provider — exclude event-only examples from the established behavior claim.** DeadLetterHandler.cs:95 populates DeadLetteredWork.FailedAt, whereas :105 populates WorkDeadLetteredEvent.Timestamp after persistence. RejectionRoutingOrchestrator.cs:271 and :278 have the same distinction. The evidence file currently groups all four as public FailedAt data. Only :95 and :271 support that statement. The other two are notifications/observability; this pass did not establish a behavioral consumer of their timestamp. Refute their use as behavioral evidence, while preserving the real clock obligation. If one captured instant is reused for both record and notification during a future fix, present that as a design choice. Do not manufacture a separate required event-clock migration from these hits.

2. **behavioral-time-provider / deterministic-time-tests — a ban proves exclusion, not the whole replacement behavior.** A policy that rejects UtcNow but permits a new Timer does not close the timer class. The final G1 must include the identified timer constructor mechanism or a separate structural check. Even a complete ban does not prove elapsed cooldown semantics or fake-timer callback correctness: preserve component assertions for cooldown, periodic evaluation and post-stop inactivity. In particular, WorkerRegistry currently orders on wall UTC; injecting a clock does not independently guarantee LIFO when clock values are equal or move backwards. Avoid promising stronger ordering semantics without an explicit sequence/monotonic design and tests.

3. **clock-through-di — distinguish established omission from contractual status.** Retain the source-confirmed omission, but state that honoring a registered provider is the proposed composition contract. No repository promise read in this pass establishes that it was previously guaranteed. The test must construct through AddWorkOrchestrator/Build and exercise priority work with the exact registered provider; resolving TimeProvider directly or calling the WorkOrchestrator constructor would miss this defect. Existing system fallback should remain covered.

4. **guard-subject-validation — constrain current failure claims.** The methods accept arbitrary disk paths privately, but current callers supply two fixed expected filenames. Missing files throw; empty current lists and actual native substitution were not observed. Keep a valid-PE/no-metadata fixture as the necessary falsifier and a wrong-managed-identity fixture for the stronger identity proposal. Do not report a demonstrated current-CI false pass until a fixture has actually run. The current code branch itself is sufficient evidence of the scanner's missing rejection behavior.

5. **short-lived-nuget-publishing — correct evidence locations and symbol failure semantics.** Current source uses secrets.NUGET_API_KEY at publish.yml:56 and :65, not :60 and :69. Symbol publishing has `continue-on-error: true` at :69. Therefore wording that every publishing failure blocks the workflow is too strong for that step. A future shared NuGet/login failure can be blocking before either loop; credential-provenance lint cannot prove remote policy activation or successful publication. Record remote activation as an open rollout condition.

6. **build-floor / scheduling-time-guard / native-aot-path / mtp-test-path — separate baseline controls from new fixes in the final summary.** Their continued existence is meaningful, but the legacy shapes listed in the ledger are mostly counterfactual regressions rather than observed debt. Existing mechanisms prevent false promotion of those counterfactuals into violations. The nonzero local architecture run verifies only its selection and command; it does not retroactively turn a failed SDK-host run or an unexecuted NativeAOT path green.

## Passes

- No full obligation was killed: each has either an established reachable legacy mechanism or a specific repository claim/control worth retaining as a baseline obligation.
- Two purported behavioral timestamp examples are refuted as evidence of FailedAt/behavior; the underlying obligation survives independently.
- Current floor supports TimeProvider; none of these recommendations needs .NET 11/C# 15.
- Build/test/native baseline controls do not duplicate clock composition or scanner input validation: each establishes a different claim.
- Eight ranked surfaces and the two-surface stopping rule are accepted as supplied; no new broad inventory was performed.

## Uncertainties

- No user scope answer, remote publishing trust configuration, external CI action body or native runtime result was available to this pass.
- Proposed guards were not present at the time of this pass and were not executed. No proposed lock-in is counted as closed.
- Test watchdogs and real external-clock integration cases require narrow exceptions; bare-delay prohibition alone cannot classify intent.
- Zero-test rejection of the advertised `dotnet test` path has not been demonstrated here. The existing SDK-host failure is indeterminate, not evidence the product or MTP configuration is wrong.
