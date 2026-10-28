# Bifrost

[![Build](https://github.com/lvlup-sw/bifrost/actions/workflows/ci.yml/badge.svg)](https://github.com/lvlup-sw/bifrost/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

Bifrost is a library for running background work through a bounded queue and a pool of workers. Define a work type and a handler, then opt into capabilities such as retries, dead-lettering, autoscaling, health checks, metrics, and priority dispatch. A separate scheduler can dispatch recurring jobs to an orchestrator or run them inline.

## How it works

An orchestrator accepts work items into a bounded queue and dispatches them to a pool of workers. Each worker invokes the handler registered for that work type. Admission confirms that an item entered the queue; it does not mean the handler has completed successfully. The default work queue is in memory and does not survive a process restart.

The default FIFO queue applies asynchronous backpressure when full. Producers can also attempt nonblocking admission. Optional priority dispatch instead rejects work immediately when its class reaches an admission watermark. An orchestrator can be drained to finish queued work or stopped by canceling its workers.

Capabilities are composed through the orchestrator's dependency-injection builder. The packages are designed for trimming and Native AOT.

## Capabilities

- **Autoscaling** adjusts the worker pool using queue utilization and configured watermarks.
- **Resilience** adds Polly policies around handler calls. The dead-letter queue has a separate retry policy for handler failures.
- **Dead-lettering** records handler failures and eligible admission rejections. Shutdown rejections and canceled enqueues are not dead-lettered. The default dead-letter queue is in memory, is bounded, and drops its oldest item when full.
- **Health checks** report orchestrator and queue health through .NET health check integration.
- **OpenTelemetry** provides queue-wait and rejection metrics, observable gauges, and instruments for handler processing metrics. Processing counters and duration require explicit recording in the handler.
- **Event stream** publishes enqueue, completion, and dead-letter events for subscribers.

## Priority dispatch

FIFO is the default. Priority dispatch can reorder work by class when latency-sensitive work shares a queue with batch work. Work can be classified at registration or supplied with a class at admission. The `bifrost.orchestrator.queue_wait` metric can help measure queue wait by class before enabling priority dispatch.

Priority dispatch uses a virtual-time key. By default, interactive work receives a 30-second boost, while batch work has no additional penalty. This limits the priority advantage in the key; actual wait times also depend on queue ordering and worker availability. Under pressure, default admission watermarks reject Batch at 90% capacity and Default at 95%, while Interactive can fill the queue. Unlike FIFO's asynchronous backpressure, priority admission rejects immediately, so producers must handle rejection.

| Binding | Ordering | Selection |
|---|---|---|
| `MultiQueue` | Relaxed two-choice priority ordering | Default for `PriorityBinding.Auto`; designed for concurrency |
| `Locking` | Exact minimum key under a global lock | Explicit opt-in when exact priority ordering matters |

MultiQueue trades ordering accuracy for concurrency; it does not guarantee that the minimum key is selected on every dequeue. See the [priority queue background](src/Bifrost.Concurrency/BACKGROUND.md) and [benchmarks](docs/benchmarks/) for the tradeoffs. Keep FIFO if you need FIFO ordering or producer-wait backpressure.

## Scheduling

`LevelUp.Bifrost.Scheduling` adds interval, cron, and one-shot scheduling with inline, orchestrator, or custom dispatch. The default schedule store is in memory, and multiple scheduler instances can produce duplicate fires; run a single scheduler process unless your deployment accounts for that behavior. See the [scheduling sample](samples/Bifrost.Scheduling.AotSmoke/Program.cs) for registration and dispatch examples.

## Packages

| Package | Purpose |
|---|---|
| `LevelUp.Bifrost.Core` | Orchestrator abstractions and options |
| `LevelUp.Bifrost` | Orchestrator, handlers, and built-in decorators |
| `LevelUp.Bifrost.Concurrency` | Concurrent priority queues, usable independently |
| `LevelUp.Bifrost.HealthChecks` | Health check integration |
| `LevelUp.Bifrost.OpenTelemetry` | Metrics integration |
| `LevelUp.Bifrost.Resilience` | Polly resilience integration |
| `LevelUp.Bifrost.Scheduling.Core` | Scheduling abstractions |
| `LevelUp.Bifrost.Scheduling` | Scheduler and dispatch implementations |
| `LevelUp.Bifrost.Scheduling.Testing` | Scheduler testing helpers |

## Build and test

Install the .NET 10 SDK; [global.json](global.json) specifies the SDK version and roll-forward policy.

```bash
git clone https://github.com/lvlup-sw/bifrost.git
cd bifrost
dotnet build src/Bifrost.sln -c Release
dotnet test --solution src/Bifrost.sln
```

Tests use TUnit and Microsoft Testing Platform. Use `dotnet test --project src/Bifrost.Tests` for one project. The full solution includes stress tests; CI runs those separately from its regular pull-request checks. The [documentation index](docs/README.md) links to design notes, research, and benchmarks. Release history is in [CHANGELOG.md](CHANGELOG.md).

## License

Apache 2.0. See [LICENSE](LICENSE).
