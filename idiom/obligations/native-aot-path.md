# Validate the claimed native shipping path

README.md:9 promises trim/AOT compatibility. All shipping projects have IsAotCompatible=true; restored evaluation enables trim/AOT analyzers. Resilience csproj enables generated configuration binding. CI references pinned external aot-smoke actions for scheduling and concurrency. This audit has a managed Release build but has not run NativeAOT publication or read the remote action implementation. Static AOT checks are evidence of a narrower claim, not end-to-end native success.

## Open questions

Native smoke execution at this revision remains unestablished (partial: sample/CI wiring and managed build inspected; native publish/run not performed).

### Investigation Log

#### Evidence and competing explanation (2026-09-17)

Read the cited production path and existing guards at revision 3a17ed0f45dfd64d953712ccda286b0915bef838. Catalog applicability is net10.0 / C# 14; no preview feature required. Investigation remains bounded as described above.

### Coverage refinement

README wording applies across packages; the two samples do not establish optional Resilience generated-binding, HealthChecks or OpenTelemetry native composition. Those paths are explicitly indeterminate alongside the unexecuted native samples. The managed build narrows, but does not settle, this claim.
