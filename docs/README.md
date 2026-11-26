# Documentation

Start with the [repository README](../README.md) for the library's capabilities, packages, and high-level design. The source and tests are the reference for current APIs and behavior.

- [Priority queue background](../src/Bifrost.Concurrency/BACKGROUND.md) explains the MultiQueue design and its tradeoffs.
- [Scheduling sample](../samples/Bifrost.Scheduling.AotSmoke/Program.cs) shows scheduler registration and dispatch modes.
- [Agent Framework sample](../samples/Bifrost.AgentFramework.Sample/README.md) and [EF Core sample](../samples/Bifrost.EntityFrameworkCore.Sample/README.md) show keyed dispatch with scoped handlers and the in-memory admission boundary. The [keyed AOT smoke](../samples/Bifrost.Keyed.AotSmoke/Program.cs) covers keyed dispatch under Native AOT.
- [Keyed dispatch release check](benchmarks/2026-11-25-keyed-dispatch-release.md) records the 0.7.0 allocation and contention benchmarks.
- [Benchmarks](benchmarks/BENCHMARKS.md) describes how to run the benchmark suite; the other files in [benchmarks](benchmarks/) record individual results.
- [Priority API benchmark runbook](benchmarks/2026-10-priority-api-benchmark-runbook.md) covers recreating, updating, and extending the Xeon CPQ report for PR #69.
- [October Xeon campaign](benchmarks/data/azure-d64sv6-2026-10-28/README.md) records the full 64-thread server run, repeated latency measurements, and source-control comparisons.
- [Automatic metrics migration](migrations/2026-11-automatic-metrics.md) explains how to move from manual `RecordProcessed` and `RecordFailed` calls to the metrics that `WithOpenTelemetry()` records automatically in 0.7.0.
- [Designs](designs/) and the [original orchestrator design](design/channel-orchestrator-library.md) capture design decisions and proposals, including the [scheduling design](designs/2026-04-10-durable-scheduling-api.md) and [priority-dispatch design](designs/2026-06-12-cpq-port-priority-dispatch.md).
- [Plans](plans/), [research](research/), and [proposals](proposals/) preserve working notes and historical context.

Dated designs, plans, and research describe the state and intentions at the time they were written. Some APIs, defaults, and proposed features may have changed; they should not be treated as current reference documentation.
