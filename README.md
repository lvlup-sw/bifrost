# Bifrost

[![Build](https://github.com/lvlup-sw/bifrost/actions/workflows/ci.yml/badge.svg)](https://github.com/lvlup-sw/bifrost/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

A high-performance, production-ready Channel-based work orchestration library for .NET 10. Provides bounded work queues, worker pool management, autoscaling, resilience, and comprehensive observability.

## Features

- **Zero-allocation hot paths** - `ValueTask`-based enqueue with no per-item allocations
- **Autoscaling** - Dynamic worker scaling based on queue utilization with configurable watermarks
- **Priority dispatch (opt-in)** - Class-aware work ordering with bounded starvation and
  admission-side load shedding; strict FIFO remains the default (see [Priority Dispatch](#priority-dispatch))
- **Resilience** - Polly integration for retry, timeout, and circuit breaker patterns
- **Health Checks** - ASP.NET Core health check integration
- **OpenTelemetry** - Metrics and tracing support
- **Event Streaming** - Pub/sub events for work lifecycle tracking

## Packages

| Package | Description | NuGet |
|---------|-------------|-------|
| `LevelUp.Bifrost.Core` | Core abstractions with zero dependencies | - |
| `LevelUp.Bifrost` | Main implementation with Channel-based orchestration | - |
| `LevelUp.Bifrost.Concurrency` | Concurrent priority queue primitives (MultiQueue + locking) | - |
| `LevelUp.Bifrost.HealthChecks` | ASP.NET Core health check integration | - |
| `LevelUp.Bifrost.OpenTelemetry` | OpenTelemetry metrics support | - |
| `LevelUp.Bifrost.Resilience` | Polly resilience integration | - |

## Quick Start

### Installation

```bash
dotnet add package LevelUp.Bifrost
dotnet add package LevelUp.Bifrost.HealthChecks  # optional
dotnet add package LevelUp.Bifrost.OpenTelemetry  # optional
dotnet add package LevelUp.Bifrost.Resilience  # optional
```

### Basic Usage

```csharp
// 1. Define your work type
public record EmailJob(string To, string Subject, string Body);

// 2. Implement a handler
public class EmailHandler : IWorkHandler<EmailJob>
{
    private readonly IEmailService _emailService;

    public EmailHandler(IEmailService emailService)
    {
        _emailService = emailService;
    }

    public async ValueTask HandleAsync(EmailJob job, CancellationToken ct)
    {
        await _emailService.SendAsync(job.To, job.Subject, job.Body, ct);
    }
}

// 3. Register services
services.AddWorkOrchestrator<EmailJob>(options =>
{
    options.Capacity = 128;
    options.WorkerCount = 2;
})
.WithHandler<EmailHandler>();

// 4. Enqueue work
public class MyService
{
    private readonly IWorkOrchestrator<EmailJob> _orchestrator;

    public MyService(IWorkOrchestrator<EmailJob> orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public async Task SendWelcomeEmailAsync(string email)
    {
        var result = await _orchestrator.EnqueueAsync(
            new EmailJob(email, "Welcome!", "Thanks for signing up!"));

        if (!result.IsAccepted)
        {
            // result.Reason: CapacityExceeded, WatermarkExceeded, or Shutdown.
            // Admission failures never throw.
        }
    }
}
```

### With Autoscaling

```csharp
services.AddWorkOrchestrator<EmailJob>(options =>
{
    options.Capacity = 128;
    options.WorkerCount = 2;
})
.WithHandler<EmailHandler>()
.WithAutoscaling(scaling =>
{
    scaling.MinWorkers = 1;
    scaling.MaxWorkers = 16;
    scaling.HighWatermark = 0.8;
    scaling.LowWatermark = 0.3;
    scaling.CooldownPeriod = TimeSpan.FromSeconds(30);
});
```

### With Health Checks

```csharp
services.AddWorkOrchestrator<EmailJob>(/* ... */)
    .WithHealthChecks();

// In your health check endpoint configuration
app.MapHealthChecks("/health");
```

### With OpenTelemetry

```csharp
services.AddWorkOrchestrator<EmailJob>(/* ... */)
    .WithOpenTelemetry();
```

### With Resilience (Polly)

```csharp
services.AddWorkOrchestrator<EmailJob>(/* ... */)
    .WithResilience(resilience =>
    {
        resilience.RetryCount = 3;
        resilience.Timeout = TimeSpan.FromSeconds(30);
        resilience.UseExponentialBackoff = true;
    });
```

## Priority Dispatch

By default all work flows through a single strict-FIFO bounded queue. When latency-sensitive
(interactive) and throughput-oriented (batch) work share an orchestrator, a batch burst queued
ahead of an interactive item adds directly to user-visible latency — and a FIFO channel cannot
reorder. Priority dispatch addresses this with class-aware ordering, but it is deliberately
**default-off, behind evidence**: instrument first, enable only when the measurements say so.

> For the theory behind the machinery — the MultiQueue algorithm, rank error, the virtual-time
> key, and watermark admission, with animated diagrams and benchmark figures — see
> [docs/research/2026-06-13-cpq-theoretical-background.md](docs/research/2026-06-13-cpq-theoretical-background.md).

### Stage 1 — instrument first (stay on FIFO)

Tag work with a `WorkClass` (`Interactive`, `Default`, `Batch`) — per enqueue or via an
options-level classifier — and enable OpenTelemetry. The `bifrost.orchestrator.queue_wait`
histogram (milliseconds, tagged `work.class`) measures what each class actually waits; the
`bifrost.orchestrator.rejected` counter (tagged `work.class`, `rejection.reason`) tracks
admission rejections.

```csharp
services.AddWorkOrchestrator<SandboxJob>(/* ... */)
    .WithHandler<SandboxHandler>()
    .WithClassifier(job => job.UserInitiated ? WorkClass.Interactive : WorkClass.Batch)
    .WithOpenTelemetry();

// Or tag per call (a per-call class other than Default wins over the classifier):
var result = await orchestrator.EnqueueAsync(job, WorkClass.Interactive);
```

Stay on FIFO until the pre-registered evidence trigger fires: **interactive-class p95
queue-wait exceeds 500 ms while batch-class work is co-resident**. The threshold is
operator-configurable — the point is to pick one *before* looking at the dashboard.

### Stage 2 — enable on evidence

```csharp
services.AddWorkOrchestrator<SandboxJob>(/* ... */)
    .WithHandler<SandboxHandler>()
    .UsePriorityDispatch(useLockingBinding: true);  // strategy choice: see below
```

Priority dispatch orders the queue by a virtual-time key (`enqueueTicks − classBoost`):
interactive work jumps at most the boost window (default 30 s) ahead, and any item that has
waited longer than the window outranks every fresh arrival — the starvation bound holds by
construction, with no aging scans. Under pressure, admission sheds the lowest class first:
Batch is rejected at 0.90 × capacity, Default at 0.95, Interactive admits to full capacity
(all configurable via `PriorityDispatchOptions`).

**Enqueue semantics change:** the priority strategies are fail-fast at admission —
`EnqueueAsync` never waits for space; at capacity or above the class watermark it rejects
immediately (producer-wait at capacity would let queued batch work block an interactive
producer, reintroducing the inversion at the admission boundary). Rejections surface in the
`EnqueueResult` and route to the dead-letter queue when one is configured. The FIFO default
keeps its producer-wait behavior.

### Choosing a strategy

Two priority bindings ship. Both use the same virtual-time key and the same watermarks; they
differ in how exactly they honor the ordering:

| Strategy | Ordering | Built for |
|---|---|---|
| `PriorityLocking` | Exact min-key dequeue under a global lock | Few workers (1–8), seconds-long work items, low queue contention |
| `PriorityMultiQueue` | Relaxed two-choice dequeue, expected rank error `(5/6)·n` (n ≈ 4 × processor count) | Many workers hammering the queue with micro work items |

Indicative soak measurements ([docs/benchmarks/2026-06-cpq-soak.md](docs/benchmarks/2026-06-cpq-soak.md),
45 s smoke runs — **not** release numbers) currently favor the **locking binding** for the
consumer-shaped regime this orchestrator typically runs in: interactive p95 queue-wait of
8.4 s vs 31.0 s at 2 workers and 1.8 s vs 17.8 s at 8 workers (locking vs MultiQueue), with
identical shed behavior, identical starvation-bound adherence, and equally flat allocations.
On a many-core host the MultiQueue's rank error is the same order as a small queue's entire
population, so class ordering washes out — its relaxation buys contended throughput
(DataFerry's published contended results: 1.7–17.5× over the lock baseline from 4 threads up),
and the low-contention regime has none to sell. The 600 s nightly soak runs
(`.github/workflows/soak.yml`) finalize this guidance; treat the numbers above as indicative
until then.

### When NOT to use priority dispatch

- **You have no measured head-of-line problem.** FIFO is the default for a reason: it keeps
  producer-wait backpressure, exact ordering, and the pre-0.5.0 semantics. Enabling priority
  dispatch on vibes trades those away for a problem you may not have.
- **You need fail-safe producer backpressure.** Priority strategies reject at admission
  instead of waiting. If your producers cannot handle rejection (even with DLQ routing),
  stay on FIFO.
- **You need strict FIFO ordering.** Priority dispatch reorders by design, and the MultiQueue
  binding additionally relaxes ordering within the priority order.
- **Your work does not run through `IWorkOrchestrator`.** Bifrost's priority dispatch (and
  its scheduling roadmap) only pay off as part of the orchestrator's
  resilience/DLQ/autoscaling/observability pipeline. For standalone job scheduling, prefer
  [NCronJob](https://github.com/NCronJob-Dev/NCronJob) (simple, in-memory) or
  [TickerQ](https://github.com/Arcenox-co/TickerQ) (durable, dashboard, multi-node) — see the
  competitive landscape in
  [docs/designs/2026-04-10-durable-scheduling-api.md](docs/designs/2026-04-10-durable-scheduling-api.md).

## API Overview

### IWorkOrchestrator<TWork>

The main interface for enqueuing work:

```csharp
public interface IWorkOrchestrator<TWork> : IAsyncDisposable
{
    // Core operations - zero-allocation hot path.
    // Admission outcomes are values, never exceptions: a rejected enqueue
    // returns EnqueueResult.Rejected(reason), it does not throw.
    ValueTask<EnqueueResult> EnqueueAsync(TWork work, WorkClass workClass = WorkClass.Default, CancellationToken ct = default);
    bool TryEnqueue(TWork work, WorkClass workClass = WorkClass.Default);

    // Synchronous variants
    void Run(TWork work, WorkClass workClass = WorkClass.Default);      // throws when full
    bool TryRun(TWork work, WorkClass workClass = WorkClass.Default);   // returns false when full

    // Observability (non-allocating property access)
    int PendingCount { get; }
    int ActiveWorkers { get; }
    int Capacity { get; }

    // Lifecycle
    Task StopAsync(CancellationToken ct = default);
    Task DrainAsync(CancellationToken ct = default);
}
```

> **Migrating from 0.4.x:** `EnqueueAsync` returned a plain `ValueTask` and the interface
> exposed a `ChannelWriter<TWork> Writer` escape hatch. The `Writer` property is gone — the
> internal queue is a pluggable dispatch-strategy binding, not necessarily a `Channel` — and
> enqueue outcomes are now reported as `EnqueueResult` values. See the
> [CHANGELOG](CHANGELOG.md) for migration snippets.

### IWorkHandler<TWork>

Implement this interface to handle work items:

```csharp
public interface IWorkHandler<TWork>
{
    ValueTask HandleAsync(TWork work, CancellationToken ct);
}
```

### Event Streaming

Subscribe to orchestrator events:

```csharp
// Enable event streaming
services.AddWorkOrchestrator<EmailJob>(/* ... */)
    .WithEventStream();

// Subscribe to events
var eventOrchestrator = serviceProvider
    .GetRequiredService<IEventStreamOrchestrator<EmailJob>>();

await foreach (var evt in eventOrchestrator.GetEventStreamAsync<WorkCompletedEvent<EmailJob>>(ct))
{
    Console.WriteLine($"Work completed in {evt.Duration}");
}
```

## Building from Source

```bash
# Clone the repository
git clone https://github.com/lvlup-sw/bifrost.git
cd bifrost

# Build
dotnet build

# Run tests
dotnet test
```

## Design

See the [design document](docs/design/channel-orchestrator-library.md) for architectural details and implementation notes.

## License

This project is licensed under the Apache License 2.0 - see the [LICENSE](LICENSE) file for details.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.
