# Bifrost

[![Build](https://github.com/lvlup-sw/bifrost/actions/workflows/ci.yml/badge.svg)](https://github.com/lvlup-sw/bifrost/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

**A work-orchestration library for .NET 10.**

Bifrost runs background work (such as emails, webhooks, and sandboxed jobs) on a pool of workers behind a bounded channel. You give it a work type and a handler; it owns admission, dispatch, retries, dead-lettering, autoscaling, and observability around them. It's built on `System.Threading.Channels`, trim- and AOT-compatible, offers a priority dispatch backed by a state-of-the-art, scalable concurrent priority queue, and is split into focused packages so you depend only on the parts you use.

## How it works

Define a work type and a handler, register them, enqueue:

```csharp
public record EmailJob(string To, string Subject, string Body);

public class EmailHandler(IEmailService email) : IWorkHandler<EmailJob>
{
    public ValueTask HandleAsync(EmailJob job, CancellationToken ct) =>
        email.SendAsync(job.To, job.Subject, job.Body, ct);
}

services.AddWorkOrchestrator<EmailJob>(o =>
{
    o.Capacity = 128;
    o.WorkerCount = 2;
})
.WithHandler<EmailHandler>();
```

Enqueue from anywhere you've injected `IWorkOrchestrator<EmailJob>`:

```csharp
var result = await orchestrator.EnqueueAsync(new EmailJob(to, "Welcome", body));
if (!result.IsAccepted)
{
    // result.Reason is CapacityExceeded, WatermarkExceeded, or Shutdown.
    // A full queue is a return value, not an exception you have to catch.
}
```

## What you compose on

Past the core queue, every capability is an opt-in decorator you add to the orchestrator.

- **Autoscaling.** Workers scale on queue utilization between a floor and a ceiling, with high/low watermarks and a cooldown.
- **Resilience.** Polly retry, timeout, and circuit breaker around each handler call.
- **Dead-letter queue.** Work that exhausts its retries, or gets rejected at admission, lands in a DLQ instead of vanishing.
- **Health checks.** ASP.NET Core health check integration.
- **OpenTelemetry.** Queue-wait and rejection metrics, tagged by work class.
- **Event stream.** Subscribe to enqueue, complete, and dead-letter events.
- **Priority dispatch.** Class-aware ordering when interactive and batch work share a queue. Off by default; see below.

```csharp
services.AddWorkOrchestrator<EmailJob>(/* ... */)
    .WithHandler<EmailHandler>()
    .WithAutoscaling(s => { s.MinWorkers = 1; s.MaxWorkers = 16; s.HighWatermark = 0.8; s.LowWatermark = 0.3; })
    .WithResilience(r => { r.RetryCount = 3; r.UseExponentialBackoff = true; })
    .WithHealthChecks()
    .WithOpenTelemetry();
```

## Packages

| Package | What it is |
|---------|------------|
| `LevelUp.Bifrost.Core` | Abstractions, zero dependencies |
| `LevelUp.Bifrost` | The orchestrator and its decorators |
| `LevelUp.Bifrost.Concurrency` | Concurrent priority queues (MultiQueue + locking), no Bifrost dependencies |
| `LevelUp.Bifrost.HealthChecks` | ASP.NET Core health checks |
| `LevelUp.Bifrost.OpenTelemetry` | Metrics |
| `LevelUp.Bifrost.Resilience` | Polly integration |

```bash
dotnet add package LevelUp.Bifrost
```

## Priority dispatch

By default everything runs through one strict-FIFO queue. With some workloads, it is useful to reorder queue elements. Priority dispatch provides this functionality with class-aware ordering.

**Measure.** Tag work with a `WorkClass` (`Interactive`, `Default`, `Batch`) and enable OpenTelemetry. The `bifrost.orchestrator.queue_wait` histogram shows what each class actually waits.

```csharp
services.AddWorkOrchestrator<SandboxJob>(/* ... */)
    .WithClassifier(job => job.UserInitiated ? WorkClass.Interactive : WorkClass.Batch)
    .WithOpenTelemetry();
```

**Enable** once a threshold you picked in advance gets crossed (say, interactive p95 queue-wait over 500 ms while batch work is co-resident with latency-sensitive work):

```csharp
services.AddWorkOrchestrator<SandboxJob>(/* ... */).UsePriorityDispatch();
```

Priority dispatch orders the queue by a virtual-time key, so interactive work jumps ahead by at most a bounded window (default 30s), and anything that has waited longer than the window outranks fresh arrivals. That window is the starvation bound. Under pressure, admission sheds the lowest class first: Batch at 0.90× capacity, Default at 0.95, Interactive to full.

Two bindings ship. `Auto` (the default) always resolves to `MultiQueue`; `Locking` never engages on its own — opt into it explicitly with `PriorityBinding.Locking` when you need exact ordering:

| Binding | Ordering | Built for |
|---|---|---|
| `Locking` | Exact min-key under a global lock | Explicit opt-in: strict rank ordering at high capacity, few workers, longer work items, low contention |
| `MultiQueue` | Relaxed two-choice, bounded rank error | The default (`Auto`): many workers hammering numerous work items |

`MultiQueue`'s relaxed ordering trades a little rank accuracy for scalability, and that rank error grows with capacity — so at high capacity, choose `Locking` explicitly if you need strict priority order. `Auto` keeps the lock-free `MultiQueue` regardless of capacity.

### When to stay on FIFO

- You have no measured head-of-line problem. FIFO keeps producer-wait backpressure and exact ordering; don't trade those away for a problem you haven't seen.
- Your producers can't handle rejection. Priority dispatch rejects at admission instead of blocking. If that doesn't work for you, even with DLQ routing, stay on FIFO.
- You need strict ordering. Priority dispatch reorders by design, and `MultiQueue` relaxes ordering further within the priority order.
- Your work doesn't run through `IWorkOrchestrator`. Bifrost earns its keep as a pipeline — resilience, DLQ, autoscaling, and observability around the queue. For standalone job scheduling, [NCronJob](https://github.com/NCronJob-Dev/NCronJob) or [TickerQ](https://github.com/Arcenox-co/TickerQ) fit better; the [scheduling design doc](docs/designs/2026-04-10-durable-scheduling-api.md) covers the comparison.

## The interface

```csharp
public interface IWorkOrchestrator<TWork> : IAsyncDisposable
{
    // Admission outcomes are values, not exceptions. A rejected enqueue returns
    // EnqueueResult.Rejected(reason); only a canceled ct throws.
    ValueTask<EnqueueResult> EnqueueAsync(TWork work, WorkClass workClass = WorkClass.Default, CancellationToken ct = default);
    bool TryEnqueue(TWork work, WorkClass workClass = WorkClass.Default);

    void Run(TWork work, WorkClass workClass = WorkClass.Default);    // throws when full
    bool TryRun(TWork work, WorkClass workClass = WorkClass.Default); // false when full

    int PendingCount { get; }
    int ActiveWorkers { get; }
    int Capacity { get; }

    Task StopAsync(CancellationToken ct = default);
    Task DrainAsync(CancellationToken ct = default);
}
```

Handlers implement one method:

```csharp
public interface IWorkHandler<TWork>
{
    ValueTask HandleAsync(TWork work, CancellationToken ct);
}
```

## Build

```bash
git clone https://github.com/lvlup-sw/bifrost.git
cd bifrost
dotnet build src/Bifrost.sln -c Release
dotnet test --solution src/Bifrost.sln
```

Tests run on TUnit over the Microsoft Testing Platform, so they take `--solution` or `--project` rather than a bare `dotnet test`. Design notes live in [docs/design](docs/design/channel-orchestrator-library.md) and [docs/designs](docs/designs/).

## License

Apache 2.0. See [LICENSE](LICENSE).
