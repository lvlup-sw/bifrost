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

# Catalog applied

Catalog version 2026-09. November-after-GA refresh is the catalog policy; no November refresh was due or simulated in this September audit. Cards are leads; only source-validated applicable claims entered the ledger.

## In-floor

- `nullable-reference-types` — net6.0 (C# 8+). Treat as current-tfm on any SDK-style project that can set LangVersion.; current capability, subject to the card’s when-not clause.
- `file-scoped-namespaces` — net6.0 / C# 10.; current capability, subject to the card’s when-not clause.
- `collection-expressions` — net8.0 / C# 12. `with(...)` arguments: C# 15 (upgrade-track unless preview/net11).; current capability, subject to the card’s when-not clause.
- `primary-constructors` — net8.0 / C# 12.; current capability, subject to the card’s when-not clause.
- `field-keyword` — net10.0 / C# 14.; current capability, subject to the card’s when-not clause.
- `extension-members` — net10.0 / C# 14. Indexers: C# 15.; current capability, subject to the card’s when-not clause.
- `params-readonlyspan` — net9.0 / C# 13 for the language. net9 libraries ship span params overloads.; current capability, subject to the card’s when-not clause.
- `pattern-matching-and-switches` — Patterns: C# 8–11 (in-floor on net6+). `closed` / `union`: C# 15 (upgrade unless preview).; current capability, subject to the card’s when-not clause.
- `top-level-statements` — net6.0 for top-level. net10.0 for file-based apps.; current capability, subject to the card’s when-not clause.
- `throw-if-null` — net6.0.; current capability, subject to the card’s when-not clause.
- `cancellation-on-public-async` — Any. Treat as current-tfm on net6+.; current capability, subject to the card’s when-not clause.
- `iasyncdisposable-await-using` — netstandard2.1 / netcoreapp3.0+. Current-tfm on net6+.; current capability, subject to the card’s when-not clause.
- `configureawait-libraries-only` — Any. `ConfigureAwaitOptions`: net8.; current capability, subject to the card’s when-not clause.
- `sdk-style-projects` — Any current SDK.; current capability, subject to the card’s when-not clause.
- `packages-config-to-packagereference` — Any SDK-style project.; current capability, subject to the card’s when-not clause.
- `central-package-management` — net8+ SDK tooling. Usable on net6+ projects with a current SDK.; current capability, subject to the card’s when-not clause.
- `implicit-usings-and-build-props` — net6.0.; current capability, subject to the card’s when-not clause.
- `net-analyzers-enabled` — Built-in from net5 SDK onward.; current capability, subject to the card’s when-not clause.
- `microsoft-testing-platform` — MTP packages on net8+. Native `dotnet test` runner in global.json: SDK 10+.; current capability, subject to the card’s when-not clause.
- `sln-slnx-and-solution-filters` — SDK 9+ for slnx; SDK 11 for slnf CLI.; current capability, subject to the card’s when-not clause.
- `file-based-apps` — net10.0.; current capability, subject to the card’s when-not clause.
- `nuget-trusted-publishing` — N/A (CI). nuget.org only.; current capability, subject to the card’s when-not clause.
- `local-sdk-pin` — `paths`: SDK 10 host. Pins themselves: any.; current capability, subject to the card’s when-not clause.
- `aot-trim-analyzers` — net7+ for LibraryImport / publish AOT.; current capability, subject to the card’s when-not clause.
- `time-via-timeprovider` — net8.0.; current capability, subject to the card’s when-not clause.
- `datetimeoffset-kind` — Any.; current capability, subject to the card’s when-not clause.
- `http-via-ihclientfactory` — net6.0 (factory is older; treat as current-tfm on netcoreapp2.1+).; current capability, subject to the card’s when-not clause.
- `searchvalues` — net8.0 for chars/bytes. net9.0 for string sets.; current capability, subject to the card’s when-not clause.
- `frozen-collections` — net8.0. Create(span): net10.; current capability, subject to the card’s when-not clause.
- `system-threading-lock` — net9.0.; current capability, subject to the card’s when-not clause.
- `generated-regex` — net7.0.; current capability, subject to the card’s when-not clause.
- `libraryimport-safehandle` — net7.0. Required for Native AOT.; current capability, subject to the card’s when-not clause.
- `stj-over-newtonsoft` — STJ: netcoreapp3.1+. Source gen: net6+.; current capability, subject to the card’s when-not clause.
- `span-first-apis` — Span: netcoreapp2.1+. Language conversions: C# 14 / net10.; current capability, subject to the card’s when-not clause.
- `tensorprimitives` — net8 for a smaller surface; net9 for the large generic surface.; current capability, subject to the card’s when-not clause.
- `obsolete-micro-opt` — Depends; net10+ for the array interface story.; current capability, subject to the card’s when-not clause.
- `guid-createversion7` — net9.0. Upgrade-track below net9.; current capability, subject to the card’s when-not clause.
- `dead-bcl-binaryformatter-webclient` — BinaryFormatter: removed/always-throws on net8+ / net9. WebClient: obsolete. Treat as current-tfm on net8+.; current capability, subject to the card’s when-not clause.
- `minimal-hosting` — net6.0.; current capability, subject to the card’s when-not clause.
- `minimal-apis-openapi` — net6.0; OpenAPI improvements through net10.; current capability, subject to the card’s when-not clause.
- `keyed-services` — net8.0.; current capability, subject to the card’s when-not clause.
- `hosted-lifecycle` — net8.0.; current capability, subject to the card’s when-not clause.
- `configuration-binding-generator` — net8.0.; current capability, subject to the card’s when-not clause.
- `no-ensurecreated-in-program` — Any EF Core.; current capability, subject to the card’s when-not clause.
- `mcp-hosting` — net8+/net10 per MCP templates.; current capability, subject to the card’s when-not clause.
- `ioptions-over-configuration-index` — Options: netcoreapp / net6+. ValidateOnStart: net8.; current capability, subject to the card’s when-not clause.
- `timeprovider-in-tests` — net8.0.; current capability, subject to the card’s when-not clause.
- `filesystem-abstraction` — Any. Package is netstandard.; current capability, subject to the card’s when-not clause.
- `no-ambient-statics` — Any.; current capability, subject to the card’s when-not clause.
- `mstest-modern-assertions` — Version-gated by MSTest package, not TFM.; current capability, subject to the card’s when-not clause.
- `no-vacuous-tests` — Any.; current capability, subject to the card’s when-not clause.
- `logger-message-source-gen` — net6.0.; current capability, subject to the card’s when-not clause.
- `otel-over-appinsights-sdk` — OTel packages on net6+. ServiceDefaults with current Aspire.; current capability, subject to the card’s when-not clause.
- `health-alive-endpoints` — ASP.NET health checks, any current.; current capability, subject to the card’s when-not clause.
- `no-raw-ai-content-in-logs` — Any.; current capability, subject to the card’s when-not clause.

## Upgrade-track

- `csharp15-unions` — net11.0 / C# 15. Always upgrade-track below that.; not required or scored as a current violation.
- `csharp15-closed-hierarchies` — net11.0 / C# 15. Upgrade-track otherwise.; not required or scored as a current violation.
- `labeled-break-continue` — C# 15. Upgrade-track otherwise.; not required or scored as a current violation.
- `memory-safety-preview` — C# 15 preview. Upgrade-track / human judgment.; not required or scored as a current violation.
- `memory-streams-net11` — net11.0. Upgrade-track otherwise.; not required or scored as a current violation.

C# 15 extensions within collection-expressions, extension-members and pattern-matching-and-switches, plus SDK 11 slnf editing, are upgrade-only subfeatures of otherwise in-floor cards. No preview recommendations were promoted. File-based-app `#:include` details and other unused catalog claims were not independently validated or used as findings.

## Not loaded

- `data-ef` — detector hit is consumer documentation, no EF implementation/package.
- `ai` — no MEAI/Semantic Kernel/OpenAI implementation.
- `aspire` — no AppHost.

## Applicability and exclusions

No SDK-format, packages.config, serializer, HTTP-constructor or nullable-disable migration was established. Existing mutable options/dictionaries, specialized per-thread queue sampling, and libraries without HTTP health endpoints meet their when-not clauses. Retry-path interpolated logging is situational: no current workload evidence makes it a required hot-path modernization. Timing, publishing and guard-integrity findings come from concrete repeated mechanisms; SDK/MTP/AOT entries also record verified or indeterminate standing claims rather than defects.
