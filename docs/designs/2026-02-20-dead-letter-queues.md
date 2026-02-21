# Design: Bifrost 0.3.0 — Dead Letter Queues

## Problem Statement

When a work item fails handler execution in the current Bifrost architecture, the exception is caught in `WorkOrchestrator.WorkerLoopAsync`, logged, and the item is **silently discarded**. There is no mechanism to preserve failed items for inspection, retry, or alerting.

Internal consumers (agentic-engine, AuthScript) need failed work items to be preserved rather than lost. Dead letter queues are the most requested feature from internal consumers.

**Success criteria:**
- Failed work items are captured with full failure context (exception, attempt count, timestamp)
- DLQ integrates with existing event stream, health check, and OpenTelemetry infrastructure
- Zero-allocation goal maintained on the happy path (DLQ only allocates on failure)
- Handler decorator pattern — no modification to core `WorkOrchestrator` internals
- 80%+ test coverage on all new code

---

## Architectural Constraint

The worker loop in `WorkOrchestrator` catches and swallows handler exceptions:

```csharp
// WorkOrchestrator.cs:211-239
catch (Exception ex)
{
    _logger.LogError(ex, "Worker {WorkerId} failed to process work item", workerId);
    // Exception swallowed — work item LOST
}
```

An orchestrator-level decorator **cannot intercept handler failures** because they're caught inside the core before any decorator sees them. Therefore, DLQ must be implemented as a **handler decorator** that wraps `IWorkHandler<TWork>`.

---

## Chosen Approach: Handler Decorator

Wrap `IWorkHandler<TWork>` with `DeadLetterHandler<TWork>` that catches exceptions, retries up to `MaxRetries`, and routes exhausted items to `IDeadLetterQueue<TWork>`. No orchestrator decorator needed — the DLQ service provides observability directly.

```
WorkerLoop:
  channel.ReadAllAsync()
    └─ try:
         DeadLetterHandler<TWork>.HandleAsync(work)
           └─ try (attempt 1..MaxRetries):
                _inner.HandleAsync(work)
              catch (all retries exhausted):
                _dlq.EnqueueAsync(deadLetteredWork)
                _notifier.Notify(event)      ← if event stream configured
                ← swallow (item preserved in DLQ, worker continues)
       catch: log (never reached for handler failures)
```

### Why Not an Orchestrator Decorator?

The roadmap originally specified `DeadLetterOrchestrator<TWork>` at decorator order 150. However, this cannot work because:
1. Handler exceptions are caught inside `WorkOrchestrator.WorkerLoopAsync` (line 227)
2. Orchestrator decorators wrap enqueue/dequeue operations, not handler execution
3. A handler decorator intercepts at the correct level — inside the try/catch that would otherwise discard the item

The handler decorator approach is simpler (one wrapping layer vs two), avoids decorator ordering complexity, and provides cleaner separation of concerns.

---

## New Types

### Bifrost.Core (abstractions)

#### DeadLetteredWork\<TWork\>

```csharp
public readonly record struct DeadLetteredWork<TWork>(
    TWork Work,
    Exception? Exception,
    int AttemptCount,
    DateTimeOffset FailedAt,
    string? CorrelationId);
```

Value type to avoid allocation on the DLQ path. Matches roadmap specification.

#### IDeadLetterQueue\<TWork\>

```csharp
public interface IDeadLetterQueue<TWork>
{
    ValueTask EnqueueAsync(DeadLetteredWork<TWork> item, CancellationToken ct = default);
    IAsyncEnumerable<DeadLetteredWork<TWork>> ReadAllAsync(CancellationToken ct = default);
    int Count { get; }
}
```

Minimal abstraction. `ReadAllAsync` is destructive (drains the queue). `Count` is for health checks and metrics. Future versions may add `PeekAsync` or pluggable backing stores.

#### DeadLetterQueueOptions

```csharp
public sealed class DeadLetterQueueOptions
{
    [Range(1, int.MaxValue)]
    public int Capacity { get; set; } = 1000;

    [Range(0, 100)]
    public int MaxRetries { get; set; } = 3;
}
```

- `Capacity`: Bounded channel size for DLQ buffer
- `MaxRetries`: Attempts before dead-lettering (0 = dead-letter on first failure)

#### WorkDeadLetteredEvent\<TWork\>

```csharp
public sealed record WorkDeadLetteredEvent<TWork>(
    TWork Work,
    Exception? Exception,
    int AttemptCount,
    DateTimeOffset Timestamp,
    string? CorrelationId = null) : ICorrelatedEvent;
```

Published to event stream subscribers when an item is dead-lettered. Implements `ICorrelatedEvent` for correlation-based filtering.

#### IDeadLetterNotifier\<TWork\>

```csharp
internal interface IDeadLetterNotifier<TWork>
{
    void Notify(WorkDeadLetteredEvent<TWork> evt);
    IDisposable Subscribe(Action<WorkDeadLetteredEvent<TWork>> callback);
}
```

Internal bridge between `DeadLetterHandler` and `EventStreamOrchestrator`. Decouples DLQ from event stream — if event stream is not configured, notifications are no-ops.

### Bifrost (implementations)

#### DeadLetterQueue\<TWork\>

```csharp
public sealed class DeadLetterQueue<TWork> : IDeadLetterQueue<TWork>
{
    private readonly Channel<DeadLetteredWork<TWork>> _channel;
    private int _count;

    public DeadLetterQueue(IOptions<DeadLetterQueueOptions> options)
    {
        _channel = Channel.CreateBounded<DeadLetteredWork<TWork>>(
            new BoundedChannelOptions(options.Value.Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest, // Oldest failures dropped when full
                SingleReader = false,
                SingleWriter = false,
            });
    }

    public int Count => Volatile.Read(ref _count);

    public async ValueTask EnqueueAsync(DeadLetteredWork<TWork> item, CancellationToken ct = default)
    {
        await _channel.Writer.WriteAsync(item, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _count);
    }

    public async IAsyncEnumerable<DeadLetteredWork<TWork>> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (_channel.Reader.TryRead(out var item))
        {
            Interlocked.Decrement(ref _count);
            yield return item;
        }
    }
}
```

**Design decisions:**
- `DropOldest` when full — newer failures are more actionable than older ones
- `ReadAllAsync` is non-blocking drain (TryRead loop, not ReadAllAsync on channel) — allows inspection without blocking
- Thread-safe count via `Interlocked`

#### DeadLetterHandler\<TWork\>

```csharp
internal sealed class DeadLetterHandler<TWork> : IWorkHandler<TWork>
{
    private readonly IWorkHandler<TWork> _inner;
    private readonly IDeadLetterQueue<TWork> _dlq;
    private readonly IDeadLetterNotifier<TWork> _notifier;
    private readonly int _maxRetries;
    private readonly ILogger<DeadLetterHandler<TWork>> _logger;

    public async ValueTask HandleAsync(TWork work, CancellationToken ct)
    {
        Exception? lastException = null;
        var attempts = 0;

        for (var i = 0; i <= _maxRetries; i++)
        {
            attempts++;
            try
            {
                await _inner.HandleAsync(work, ct).ConfigureAwait(false);
                return; // Success — exit immediately
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Never dead-letter on cancellation
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogWarning(ex,
                    "Handler failed for work item (attempt {Attempt}/{MaxRetries})",
                    attempts, _maxRetries + 1);
            }
        }

        // All retries exhausted — dead-letter the item
        var deadLetter = new DeadLetteredWork<TWork>(
            work, lastException, attempts, DateTimeOffset.UtcNow, CorrelationId: null);

        await _dlq.EnqueueAsync(deadLetter, ct).ConfigureAwait(false);

        _notifier.Notify(new WorkDeadLetteredEvent<TWork>(
            work, lastException, attempts, DateTimeOffset.UtcNow));

        _logger.LogError(lastException,
            "Work item dead-lettered after {Attempts} attempts", attempts);
    }
}
```

**Key behaviors:**
- Retries are immediate (no backoff) — users who need backoff can compose with Polly in their handler
- `OperationCanceledException` is never caught/dead-lettered — cancellation means shutdown
- After dead-lettering, the exception is **not re-thrown** — the worker loop continues processing the next item
- The item is preserved in the DLQ, not lost

#### DeadLetterNotifier\<TWork\>

```csharp
internal sealed class DeadLetterNotifier<TWork> : IDeadLetterNotifier<TWork>
{
    private Action<WorkDeadLetteredEvent<TWork>>? _subscriber;

    public void Notify(WorkDeadLetteredEvent<TWork> evt) => _subscriber?.Invoke(evt);

    public IDisposable Subscribe(Action<WorkDeadLetteredEvent<TWork>> callback)
    {
        _subscriber = callback;
        return new Subscription(() => _subscriber = null);
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
```

Simple single-subscriber notification bridge. Wired by builder if event stream is configured.

### Builder Extension

#### DeadLetterQueueExtensions

```csharp
public static class DeadLetterQueueExtensions
{
    public static WorkOrchestratorBuilder<TWork> WithDeadLetterQueue<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Action<DeadLetterQueueOptions>? configure = null)
    {
        builder.Services.AddOptions<DeadLetterQueueOptions>()
            .Configure(configure ?? (_ => { }))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.TryAddSingleton<IDeadLetterQueue<TWork>, DeadLetterQueue<TWork>>();
        builder.Services.TryAddSingleton<IDeadLetterNotifier<TWork>, DeadLetterNotifier<TWork>>();

        builder.HandlerDecorators.Add((sp, inner) => new DeadLetterHandler<TWork>(
            inner,
            sp.GetRequiredService<IDeadLetterQueue<TWork>>(),
            sp.GetRequiredService<IDeadLetterNotifier<TWork>>(),
            sp.GetRequiredService<IOptions<DeadLetterQueueOptions>>(),
            sp.GetRequiredService<ILogger<DeadLetterHandler<TWork>>>()));

        return builder;
    }
}
```

**Requires builder modification**: Add `HandlerDecorators` list to `WorkOrchestratorBuilder<TWork>` and apply them in `Build()` before creating `WorkOrchestrator`.

### Builder Modification

```csharp
// WorkOrchestratorBuilder<TWork> additions:
internal List<Func<IServiceProvider, IWorkHandler<TWork>, IWorkHandler<TWork>>> HandlerDecorators { get; } = [];

// In Build(), before creating WorkOrchestrator:
var handler = sp.GetRequiredService<IWorkHandler<TWork>>();
foreach (var decorator in HandlerDecorators)
{
    handler = decorator(sp, handler);
}
var orchestrator = new WorkOrchestrator<TWork>(handler, channel, options, logger);
```

### Bifrost.HealthChecks

#### DeadLetterQueueHealthCheck\<TWork\>

```csharp
public sealed class DeadLetterQueueHealthCheck<TWork> : IHealthCheck
{
    private readonly IDeadLetterQueue<TWork> _dlq;
    private readonly int _degradedThreshold;
    private readonly int _unhealthyThreshold;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var count = _dlq.Count;

        if (count >= _unhealthyThreshold)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"DLQ critical: {count} dead-lettered items"));

        if (count >= _degradedThreshold)
            return Task.FromResult(HealthCheckResult.Degraded(
                $"DLQ elevated: {count} dead-lettered items"));

        return Task.FromResult(HealthCheckResult.Healthy(
            $"DLQ: {count} dead-lettered items"));
    }
}
```

**Registration:** Automatically added when both `.WithDeadLetterQueue()` and `.WithHealthChecks()` are configured. Thresholds configurable via `DeadLetterHealthCheckOptions`.

### Bifrost.OpenTelemetry

Add to existing `OrchestratorMetrics<TWork>`:

```csharp
// New counter
public Counter<long> ItemsDeadLettered { get; }

ItemsDeadLettered = _meter.CreateCounter<long>(
    "orchestrator.items.deadlettered",
    unit: "{item}",
    description: "Total number of work items sent to dead letter queue");

// New observable gauge
public ObservableGauge<int> DeadLetterQueueDepth { get; }

DeadLetterQueueDepth = _meter.CreateObservableGauge(
    "orchestrator.dlq.depth",
    () => dlqProvider()?.Count ?? 0,
    unit: "{item}",
    description: "Current dead letter queue depth");
```

---

## Integration Matrix

| Feature | Integration | How |
|---------|------------|-----|
| **Event Stream** | `WorkDeadLetteredEvent<TWork>` published to subscribers | `IDeadLetterNotifier<TWork>` bridge wired by builder when both configured |
| **Health Checks** | DLQ depth health check | `DeadLetterQueueHealthCheck<TWork>` registered alongside orchestrator health check |
| **OpenTelemetry** | `items.deadlettered` counter, `dlq.depth` gauge | Added to `OrchestratorMetrics<TWork>` with lazy DLQ provider |
| **Resilience** | Independent | Resilience wraps EnqueueAsync (queue full), DLQ wraps handler (execution failure) — orthogonal concerns |
| **Autoscaling** | Transparent | Autoscaling counts all enqueue attempts; DLQ operates inside worker loop, invisible to autoscaling |

---

## Usage

### Basic

```csharp
services.AddWorkOrchestrator<EmailJob>(opts =>
    {
        opts.Capacity = 1024;
        opts.WorkerCount = 4;
    })
    .WithHandler<EmailHandler>()
    .WithDeadLetterQueue(dlq =>
    {
        dlq.Capacity = 1000;
        dlq.MaxRetries = 3;
    })
    .Build();
```

### Full Stack

```csharp
services.AddWorkOrchestrator<EmailJob>(opts => { ... })
    .WithHandler<EmailHandler>()
    .WithDeadLetterQueue(dlq =>
    {
        dlq.Capacity = 1000;
        dlq.MaxRetries = 3;
    })
    .WithEventStream()
    .WithAutoscaling()
    .WithHealthChecks()
    .WithOpenTelemetry()
    .Build();
```

### Inspecting the DLQ

```csharp
public class DlqInspectorService(IDeadLetterQueue<EmailJob> dlq) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), ct);

            await foreach (var item in dlq.ReadAllAsync(ct))
            {
                logger.LogWarning("Dead-lettered: {Work}, Exception: {Exception}, Attempts: {Attempts}",
                    item.Work, item.Exception?.Message, item.AttemptCount);
            }
        }
    }
}
```

### Subscribing to DLQ Events

```csharp
// Via event stream (if configured)
await foreach (var evt in orchestrator.GetEventStreamAsync<WorkDeadLetteredEvent<EmailJob>>(ct))
{
    alertService.SendAlert($"Email job dead-lettered: {evt.Work}");
}
```

---

## File Structure

```
src/Bifrost.Core/
├── DeadLetter/
│   ├── DeadLetteredWork.cs              # readonly record struct
│   ├── DeadLetterQueueOptions.cs        # Options with validation
│   └── IDeadLetterQueue.cs              # Abstraction
├── Events/
│   └── WorkDeadLetteredEvent.cs         # New event type

src/Bifrost/
├── DeadLetter/
│   ├── DeadLetterQueue.cs               # Channel-backed implementation
│   ├── DeadLetterHandler.cs             # Handler decorator
│   └── DeadLetterNotifier.cs            # Event bridge (internal)
├── DependencyInjection/
│   ├── DeadLetterQueueExtensions.cs     # .WithDeadLetterQueue()
│   └── WorkOrchestratorBuilder.cs       # Modified: add HandlerDecorators

src/Bifrost.HealthChecks/
│   └── DeadLetterQueueHealthCheck.cs    # DLQ depth health check

src/Bifrost.OpenTelemetry/
│   └── OrchestratorMetrics.cs           # Modified: add DLQ counter + gauge

src/Bifrost.Tests/
├── DeadLetter/
│   ├── DeadLetterQueueTests.cs
│   ├── DeadLetterHandlerTests.cs
│   └── DeadLetterNotifierTests.cs
├── DependencyInjection/
│   └── DeadLetterQueueExtensionsTests.cs
├── Events/
│   └── WorkDeadLetteredEventTests.cs
└── HealthChecks/
    └── DeadLetterQueueHealthCheckTests.cs

src/Bifrost.Benchmarks/
├── DeadLetter/
│   └── DeadLetterQueueBenchmarks.cs     # Enqueue/drain latency & allocation
```

---

## 0.3.0 Full Scope

### DLQ Feature (primary)

- [ ] `DeadLetteredWork<TWork>` record struct (Bifrost.Core)
- [ ] `IDeadLetterQueue<TWork>` interface (Bifrost.Core)
- [ ] `DeadLetterQueueOptions` with validation (Bifrost.Core)
- [ ] `WorkDeadLetteredEvent<TWork>` event type (Bifrost.Core)
- [ ] `DeadLetterQueue<TWork>` Channel-backed implementation (Bifrost)
- [ ] `DeadLetterHandler<TWork>` handler decorator (Bifrost)
- [ ] `DeadLetterNotifier<TWork>` event bridge (Bifrost, internal)
- [ ] `.WithDeadLetterQueue()` builder extension (Bifrost)
- [ ] `WorkOrchestratorBuilder<TWork>` modification — `HandlerDecorators` support
- [ ] `DeadLetterQueueHealthCheck<TWork>` (Bifrost.HealthChecks)
- [ ] DLQ metrics in `OrchestratorMetrics<TWork>` (Bifrost.OpenTelemetry)
- [ ] DLQ benchmarks (Bifrost.Benchmarks)
- [ ] Unit tests — target 80% coverage on new code

### Benchmark Backfill (secondary)

- [ ] `PropertyAccessBenchmarks.cs` — PendingCount, ActiveWorkers, Capacity
- [ ] `EventStreamOverheadBenchmarks.cs` — Channel broadcast cost
- [ ] `WorkerRegistryBenchmarks.cs` — Registry operations
- [ ] `HealthCheckBenchmarks.cs` — Health check execution latency

### Housekeeping

- [ ] Close Issue #2 (format-check CI already merged via PR #3)
- [ ] Cut `v0.2.0` tag on main

---

## Testing Strategy

### Unit Tests

| Test Class | Covers |
|-----------|--------|
| `DeadLetterQueueTests` | Enqueue, drain, count, capacity behavior, DropOldest when full |
| `DeadLetterHandlerTests` | Retry logic, dead-lettering after exhaustion, cancellation pass-through, notification |
| `DeadLetterNotifierTests` | Subscribe/notify, no-subscriber no-op, dispose cleanup |
| `DeadLetterQueueExtensionsTests` | DI registration, handler wrapping, options validation |
| `WorkDeadLetteredEventTests` | Record equality, ICorrelatedEvent implementation |
| `DeadLetterQueueHealthCheckTests` | Healthy/Degraded/Unhealthy thresholds |

### Integration Scenarios

| Scenario | Validates |
|----------|-----------|
| Handler throws → item appears in DLQ | End-to-end DLQ flow |
| Handler throws → retries N times → dead-letters | Retry exhaustion |
| Handler throws → WorkDeadLetteredEvent published | Event stream integration |
| DLQ at capacity → DropOldest behavior | Bounded channel semantics |
| Cancellation during handler → not dead-lettered | Cancellation safety |
| DLQ count → health check reports Degraded/Unhealthy | Health check integration |

### Benchmark Targets

| Benchmark | Target |
|-----------|--------|
| DLQ enqueue latency | < 200ns (Channel.WriteAsync) |
| DLQ drain latency per item | < 100ns (TryRead) |
| Happy path overhead (no failure) | < 5ns (try/catch cost only) |
| DLQ enqueue allocations | Minimal (record struct + Channel overhead) |

---

## Open Questions (Resolved)

| Question | Resolution |
|----------|-----------|
| DLQ persistence? | In-memory (Channel-backed) through 0.5.0. Pluggable backing stores deferred. |
| Batch failure granularity? | Deferred to 0.4.0 (batch processing release). |
| Decorator vs handler pattern? | Handler decorator — architectural constraint makes orchestrator decorator impossible for failure interception. |
| Event publishing mechanism? | `IDeadLetterNotifier<TWork>` bridge, wired by builder. |
| Retry strategy? | Immediate retries with `MaxRetries` count. Advanced strategies (backoff) via Polly in user handler. |

---

## Updated Roadmap Adjustment

Based on 0.2.0 gap analysis:

| Release | Scope Change |
|---------|-------------|
| **0.3.0** | DLQ + benchmark backfill (4 classes) + housekeeping |
| **0.4.0** | Batch processing + VitePress scaffold (deferred from 0.2.0) + EndToEnd benchmarks (deferred from 0.2.0) |
| **0.5.0** | Stabilization + Priority Queues (unchanged) |
