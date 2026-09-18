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

# Survey

## Stage 0 and ranked scope

Selection is consequence (1–5) multiplied by exposure (1–5), with ties resolved by breadth of consumers. Eight surfaces is the budget, not a findings quota. Continue until two consecutive surfaces yield no obligation surviving refutation. This is a baseline of inspected mechanisms, not a claim that every line was reviewed.

| Rank | Surface | C x E | Reason |
|---|---|---|---|
| 1 | Build, package management, CI and NuGet publication | 5 x 5 | Every consumer depends on enforcement and shipped artifacts. |
| 2 | Orchestrator, autoscaling, dead-letter and admission time | 5 x 5 | Every enqueue and worker lifecycle can cross these paths. |
| 3 | Scheduling, store/dispatcher contracts and clock/AOT architecture guards | 5 x 4 | Durable scheduling and background dispatch. |
| 4 | Concurrent priority queue and AOT sample | 5 x 4 | Shared dispatch implementation and unsafe/layout choices. |
| 5 | Public core contracts and DI composition | 4 x 4 | Published NuGet API and consumer construction. |
| 6 | TUnit projects and deterministic timing tests | 4 x 3 | Existing enforcement and reproducibility. |
| 7 | Resilience, telemetry and health adapters | 3 x 3 | Optional integrations; generated binding and factory seams. |
| 8 | Benchmarks, driver and documentation claims | 2 x 2 | Non-shipping workloads and modernization claims. |

Detector ran before catalog loading. `floor-before-restore.json` is retained to show that missing NuGet imports initially made warnings-as-errors look false. Restore succeeded from available packages; `floor.json` is authoritative after restore. SDK actually used: 10.0.303, not the minimum 10.0.100 string in global.json. The detector's EF domain hit is documentation of consumers' DbContext in ScopedHandlerProxy/HandlerExtensions, not an EF package or implementation. Hosting/DI is present even though the detector missed it. Loaded language, project-sdk, bcl-performance, hosting-di, testing, observability. No EF/AI/Aspire catalog loaded.

Missing injected context: `/home/reedsalus/.Codex/lvlup/lvlup.md` and repository `USER-CONTEXT.md` do not exist. Search did not locate replacements. No instructions invented for them.

## Lens 1 — mechanism

All 16 discovered projects are SDK-style net10.0. src and samples import one CPM authority (`samples/Directory.Packages.props:4`, `src/Directory.Packages.props:3`); the benchmark driver has no direct PackageReference. Library entry points flow through WorkOrchestratorBuilder.Build, handler/decorator factories, queue bindings and the worker handler. Scheduling registers a host, registry, dispatcher router and TimeProvider. No production legacy serializer/HTTP construction or EF implementation found by seed scan. AOT is declared on shipping libraries. TUnit 1.2.11 and MTP are configured.

## Lens 2 — intent and claims (verbatim corpus)

- C1 README.md:7: “A work-orchestration library for .NET 10.”
- C2 README.md:9: “trim- and AOT-compatible”.
- C3 README.md:155: “Tests run on TUnit over the Microsoft Testing Platform, so they take `--solution` or `--project` rather than a bare `dotnet test`.”
- C4 docs/designs/2026-04-10-durable-scheduling-api.md:220: “The scheduler uses `TimeProvider` for all time operations (no `DateTimeOffset.UtcNow`, no `Task.Delay` without `TimeProvider`) to support `FakeTimeProvider` in tests.”
- C5 CLAUDE.md:29: “analyzers/warnings-as-errors flow in from the internal `Lvlup.Build` package.”
- C6 CLAUDE.md:27: “Central Package Management — bump versions in `src/Directory.Packages.props` only, never in a `.csproj`.” (Markdown emphasis removed; wording retained.)
- C7 src/Bifrost/Handlers/ScopedHandlerProxy.cs:26–27 promises async scope creation and disposal after each handler, including exceptions.

C1–C6 are exact excerpts, not evidence of compliance. C7 is a paraphrased claim. Scope answer remains unanswered; assumed repository-wide scope is explicitly not quoted as an answer.

## Lens 3 — authority topology

Time has two authorities on the same logical orchestration surface: injected TimeProvider in WorkOrchestrator and Scheduling, and wall clock / Timer in AutoscalingEngine, WorkerInfo and dead-letter decorators. Scheduling guard policy is represented twice: BannedSymbols.txt plus hardcoded metadata tuples/signature logic in BannedTimeApiTests. AOT guard policy likewise appears as symbols and scanner code. Existing scanner fixture validates only DateTime.UtcNow, not all policy branches. Package versions have one source for shipped/sample projects; no CPM split promoted. Options remain mutable for configuration binding; records are not automatically an improvement.

## Lens 4 — production path

`AddWorkOrchestrator` → `WorkOrchestratorBuilder.Build` → new WorkOrchestrator at line 148 omits its optional TimeProvider. Constructor line 123 therefore chooses TimeProvider.System even if DI has a custom clock. Priority latency measurement reads that field. `WithAutoscaling` line 73 registers AutoscalingEngine; its constructor creates System.Threading.Timer at line 87 and EvaluateScaling reads UtcNow at 192. WorkerRegistry orders by WorkerInfo.CreatedAt (141). `WithDeadLetterQueue` constructs DeadLetterHandler; rejected admission is wired through RejectionRoutingRegistration. Their FailedAt values are public queue data, not merely log timestamps. Scheduling uses TryAddSingleton(TimeProvider.System), and testing replaces that descriptor. ScopedHandlerProxy uses CreateAsyncScope/await using; no captive-scope finding.

## Lens 5 — existing proof inventory

Rung 1: shared nullable, CPM, AOT properties, imported Lvlup.Build warnings-as-errors. Rung 3: BannedApiAnalyzers 4.14.0 in Scheduling/Core and Concurrency; scheduling clock and AOT metadata scanners in Architecture category. Rung 4: FakeTimeProvider scheduler tests, autoscaling component tests (some real waits), scoped-handler tests. Rung 5: two CI AOT smoke sample invocations. CI build-test action receives `src/Bifrost.Tests*/*.csproj`; stress is separately gated. Remote action implementation and branch protection are not established. Publish workflow tests only `src/Bifrost.Tests/*.csproj`, then builds without test hooks and packs. This is a separate release-coverage question, not proof MTP itself is wrong.

## Lens 6 — history and recurrence seed

History inspected: HEAD 3a17ed0 and preceding 11 commits; paths for scheduling banned symbols and AutoscalingEngine. Scheduling design DR-7 explicitly records an earlier relative-time factory mistake and requires a guard. Current repeated legacy classes: (R1) time statics in AutoscalingEngine:192 and WorkerInfo:41, plus public DLQ data at DeadLetterHandler:95 / RejectionRoutingOrchestrator:271; (R2) elapsed-time test sleeps at AutoscalingEngineTests:296 and AutoscalingEngineEventsTests:41,65,127,152,190,232; (R3) `!HasMetadata -> continue` in BannedTimeApiTests:143,208 and AotSafetyTests:164; (R4) stored publishing credential used in both package and symbol pushes, publish.yml:56,65. No broad claim of chronological reintroduction is made from this bounded history.

## Lens 7 — wildcard

Potentially strongest false confidence: SDK property evaluation before NuGet restore omits Lvlup.Build imports. Re-evaluation refutes the tentative disabled-warning finding. EF detector lead also refuted by real dependency inspection. ThreadStatic in the priority queue is documented per-thread random sampling state, not request context; Unsafe.As supports inline buffer representation rather than an evidenced obsolete bounds-check optimization. No algorithm rewrite inferred from newer language availability.

## Stage 2 lens passes

1. Cosmetic modernity: promote ambient behavioral time and timing sleeps; retain imported analyzer floor as a baseline rather than a defect.
2. Promise against delivery: map C1/C5/C6 to build-floor; C2 to native-aot-path; C3 to mtp-test-path; C4 to scheduling-time-guard; C7 to refuted captive-scopes.
3. Representable invalid states: inspect EnqueueResult (default struct is false/null despite factory invariants), JobDescriptor and mutable Options. EnqueueResult default is a public API design question; no production path returning default found, so do not elevate to reachable runtime debt. No mandate for C# 15 unions.
4. Integration completeness: independently trace omitted clock at WorkOrchestratorBuilder:148–152; separate composition obligation from banned APIs.
5. Upgrade/compatibility: published NuGet constructors require overload-preserving remediation; no TFM upgrade needed. NuGet stored key → trusted publishing is a current CI improvement. Preview catalog remains deferred.
6. Claim derivation: all C1–C7 dispositions listed above; AOT path awaits bound native publish proof, not inferred from analyzer flags.
7. Recurrence to guard: convert R1/R2 to scoped BannedApi specifications; R3 to fail-closed scanner checks with bad-subject self-tests; R4 to parsed workflow guard against stored credentials.
8. Wildcard/obsolete micro-optimization: inspect Concurrency ThreadHandle/SubQueueBuffer/PaddedTopSlot, benchmark driver and HealthChecks dictionaries; no evidence supports deleting specialized concurrency structures or freezing per-request maps. No new obligation.
