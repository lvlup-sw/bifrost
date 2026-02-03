# Design: Channel Orchestrator Library

## Problem Statement

The agentic-engine codebase contains a robust, production-tested Channel-based background task orchestration infrastructure (`TaskOrchestrator`, `AutoscalingEngine`, `AutoscalingTaskOrchestrator`). This infrastructure solves common problems: bounded work queues, worker pool management, autoscaling, resilience, and observability.

These patterns are applicable across multiple projects—from server workloads to game engines. Extracting this into a standalone, general-purpose library will:

1. Enable reuse across projects without copy-paste
2. Allow focused optimization for high-performance scenarios
3. Provide a polished, "batteries included" developer experience
4. Establish a foundation comparable to BCL offerings in quality

**Success criteria:**
- Zero-allocation hot paths in steady state
- Aspire-style discoverable API (`Add*()` / `With*()` patterns)
- BCL-aligned runtime API (feels like `Channel<T>`)
- Decorator-based extensibility preserved internally
- Comprehensive health checks and OpenTelemetry integration

---

## Chosen Approach

**Aspire-Style Configuration + Decorator Internals + BCL-Style Runtime API**

The library presents a fluent, discoverable configuration API while internally leveraging the proven decorator pattern for composing cross-cutting concerns. At runtime, the API surface is minimal and Channel-like for maximum performance.

### Configuration API (Aspire-Style)

```csharp
services.AddWorkOrchestrator<TWork>(options =>
{
    options.Capacity = 128;
    options.WorkerCount = 2;
})
.WithAutoscaling(scaling =>
{
    scaling.MinWorkers = 1;
    scaling.MaxWorkers = 16;
    scaling.HighWatermark = 0.8;
    scaling.LowWatermark = 0.3;
    scaling.CooldownPeriod = TimeSpan.FromSeconds(30);
})
.WithResilience(resilience =>
{
    resilience.RetryCount = 3;
    resilience.Timeout = TimeSpan.FromSeconds(30);
    resilience.UseExponentialBackoff = true;
})
.WithEventStream()
.WithHealthChecks()
.WithOpenTelemetry();
```

### Runtime API (BCL-Style)

```csharp
public interface IWorkOrchestrator<TWork> : IAsyncDisposable
{
    // Core operations - zero-allocation hot path
    ValueTask EnqueueAsync(TWork work, CancellationToken ct = default);
    bool TryEnqueue(TWork work);

    // Observability (non-allocating property access)
    int PendingCount { get; }
    int ActiveWorkers { get; }
    int Capacity { get; }

    // Lifecycle
    Task StopAsync(CancellationToken ct = default);

    // Escape hatch for advanced scenarios
    ChannelWriter<TWork> Writer { get; }
}
```

### Work Handler Contract

```csharp
public interface IWorkHandler<TWork>
{
    ValueTask HandleAsync(TWork work, CancellationToken ct);
}
```

---

## Technical Design

### Package Structure

```
Bifrost/
├── Bifrost.Core/                    # Core abstractions, zero dependencies
│   ├── IWorkOrchestrator.cs
│   ├── IWorkHandler.cs
│   ├── WorkOrchestratorOptions.cs
│   └── Events/
│       ├── IOrchestratorEvent.cs
│       └── Built-in event types
│
├── Bifrost/                         # Main package, depends on Core
│   ├── WorkOrchestrator.cs          # Channel-based implementation
│   ├── WorkerPool.cs                # Worker lifecycle management
│   ├── Decorators/
│   │   ├── AutoscalingOrchestrator.cs
│   │   └── ResilientOrchestrator.cs
│   ├── Autoscaling/
│   │   ├── AutoscalingEngine.cs
│   │   ├── AutoscalingCoordinator.cs
│   │   ├── WorkerRegistry.cs
│   │   ├── WorkerMetrics.cs
│   │   └── ScalingDecision.cs
│   └── DependencyInjection/
│       ├── WorkOrchestratorBuilder.cs
│       └── ServiceCollectionExtensions.cs
│
├── Bifrost.Resilience/              # Polly integration (optional)
│   ├── ResilientOrchestrator.cs
│   └── ResilienceExtensions.cs
│
├── Bifrost.HealthChecks/            # Health check integration (optional)
│   └── HealthCheckExtensions.cs
│
├── Bifrost.OpenTelemetry/           # Telemetry integration (optional)
│   └── TelemetryExtensions.cs
│
└── Bifrost.Tests/                   # TUnit tests
```

### Decorator Chain Architecture

```
User requests IWorkOrchestrator<TWork>
                    │
                    ▼
┌─────────────────────────────────────────────────────────────────┐
│        Decorator Chain (built by extensions)                    │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │     ResilientOrchestrator<TWork>                          │  │  ← WithResilience()
│  │  ┌─────────────────────────────────────────────────────┐  │  │
│  │  │   AutoscalingOrchestrator<TWork>                    │  │  │  ← WithAutoscaling()
│  │  │  ┌───────────────────────────────────────────────┐  │  │  │
│  │  │  │   EventStreamOrchestrator<TWork>              │  │  │  │  ← WithEventStream()
│  │  │  │  ┌─────────────────────────────────────────┐  │  │  │  │
│  │  │  │  │   WorkOrchestrator<TWork>               │  │  │  │  │  ← Core (always present)
│  │  │  │  └─────────────────────────────────────────┘  │  │  │  │
│  │  │  └───────────────────────────────────────────────┘  │  │  │
│  │  └─────────────────────────────────────────────────────┘  │  │
│  └───────────────────────────────────────────────────────────┘  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### Builder Implementation

```csharp
public sealed class WorkOrchestratorBuilder<TWork>
{
    private readonly IServiceCollection _services;
    private readonly List<DecoratorRegistration<TWork>> _decorators = [];

    internal WorkOrchestratorBuilder(IServiceCollection services)
    {
        _services = services;
    }

    public WorkOrchestratorBuilder<TWork> WithAutoscaling(
        Action<AutoscalingOptions>? configure = null)
    {
        _services.Configure<AutoscalingOptions>(configure ?? (_ => { }));

        // Register autoscaling infrastructure
        _services.TryAddSingleton<IWorkerRegistry, WorkerRegistry>();
        _services.TryAddSingleton<IWorkerMetrics, WorkerMetrics>();
        _services.TryAddSingleton<IAutoscalingEngine, AutoscalingEngine>();

        // Register coordinator for circular dependency resolution
        _services.TryAddSingleton<IAutoscalingCoordinator<TWork>>(sp =>
            new AutoscalingCoordinator<TWork>(
                sp.GetRequiredService<IWorkOrchestrator<TWork>>()));

        _decorators.Add(new DecoratorRegistration<TWork>(
            order: 100, // Autoscaling wraps resilience
            factory: (sp, inner) => new AutoscalingOrchestrator<TWork>(
                inner,
                sp.GetRequiredService<IWorkerMetrics>(),
                sp.GetRequiredService<IWorkerRegistry>(),
                sp.GetRequiredService<IOptions<AutoscalingOptions>>(),
                sp.GetRequiredService<ILogger<AutoscalingOrchestrator<TWork>>>())));

        return this;
    }

    internal void Build()
    {
        // Sort decorators by order (innermost first)
        var orderedDecorators = _decorators.OrderBy(d => d.Order).ToList();

        _services.AddSingleton<IWorkOrchestrator<TWork>>(sp =>
        {
            // Start with base implementation
            IWorkOrchestrator<TWork> orchestrator =
                new WorkOrchestrator<TWork>(
                    sp.GetRequiredService<IWorkHandler<TWork>>(),
                    sp.GetRequiredService<IOptions<WorkOrchestratorOptions>>(),
                    sp.GetRequiredService<ILogger<WorkOrchestrator<TWork>>>());

            // Apply decorators in order
            foreach (var registration in orderedDecorators)
            {
                orchestrator = registration.Factory(sp, orchestrator);
            }

            return orchestrator;
        });
    }
}

// Decorator ordering record
internal sealed record DecoratorRegistration<TWork>(
    int Order,
    Func<IServiceProvider, IWorkOrchestrator<TWork>, IWorkOrchestrator<TWork>> Factory);
```

### Core WorkOrchestrator Implementation

```csharp
public sealed class WorkOrchestrator<TWork> : IWorkOrchestrator<TWork>
{
    private readonly Channel<TWork> _channel;
    private readonly IWorkHandler<TWork> _handler;
    private readonly Task[] _workers;
    private readonly CancellationTokenSource _cts = new();

    public WorkOrchestrator(
        IWorkHandler<TWork> handler,
        IOptions<WorkOrchestratorOptions> options,
        ILogger<WorkOrchestrator<TWork>> logger)
    {
        _handler = handler;

        var opts = options.Value;
        _channel = Channel.CreateBounded<TWork>(new BoundedChannelOptions(opts.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false // Prevent stack dives
        });

        // Start worker tasks
        _workers = Enumerable.Range(0, opts.WorkerCount)
            .Select(i => Task.Factory.StartNew(
                () => WorkerLoopAsync($"Worker-{i}", _cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap())
            .ToArray();
    }

    // Zero-allocation enqueue (struct-based ValueTask)
    public ValueTask EnqueueAsync(TWork work, CancellationToken ct = default)
    {
        return _channel.Writer.WriteAsync(work, ct);
    }

    public bool TryEnqueue(TWork work)
    {
        return _channel.Writer.TryWrite(work);
    }

    public int PendingCount => _channel.Reader.CanCount ? _channel.Reader.Count : 0;
    public int ActiveWorkers => _workers.Length;
    public int Capacity { get; }

    public ChannelWriter<TWork> Writer => _channel.Writer;

    private async Task WorkerLoopAsync(string workerId, CancellationToken ct)
    {
        await foreach (var work in _channel.Reader.ReadAllAsync(ct))
        {
            await _handler.HandleAsync(work, ct);
        }
    }

    // ... StopAsync, DisposeAsync implementations
}
```

### Performance Optimizations

| Optimization | Technique |
|--------------|-----------|
| Zero-allocation enqueue | `ValueTask` from `WriteAsync`, no lambda capture |
| No per-item allocations | Work items flow through channel without wrapping |
| Struct-based events | `readonly record struct` for event types |
| Object pooling | Pool `WorkerContext` objects for autoscaling workers |
| Lock-free metrics | `Interlocked` operations for counters |
| Bounded channels | Backpressure prevents memory growth |

### Event System (Opt-in)

```csharp
// Events are readonly record structs for zero-allocation
public readonly record struct WorkEnqueuedEvent<TWork>(
    TWork Work,
    DateTimeOffset Timestamp,
    int QueueDepth);

public readonly record struct WorkCompletedEvent<TWork>(
    TWork Work,
    TimeSpan Duration,
    bool Success);

public readonly record struct ScalingEvent(
    ScalingAction Action,
    int PreviousWorkers,
    int CurrentWorkers,
    double Utilization);

// Event stream interface (only present with WithEventStream())
public interface IEventStreamOrchestrator<TWork> : IWorkOrchestrator<TWork>
{
    IAsyncEnumerable<TOrchestratorEvent> GetEventStreamAsync<TOrchestratorEvent>(
        CancellationToken ct = default) where TOrchestratorEvent : IOrchestratorEvent;
}
```

---

## Integration Points

### Migration from Existing TaskOrchestrator

The existing `TaskOrchestrator` API uses `Func<Task>` delegates. Migration path:

```csharp
// Current API
orchestrator.Run(async () => await ProcessAsync(item));

// New API - define work type
public record EmailJob(string To, string Subject, string Body);

// Implement handler
public class EmailHandler : IWorkHandler<EmailJob>
{
    public async ValueTask HandleAsync(EmailJob job, CancellationToken ct)
    {
        await _emailService.SendAsync(job.To, job.Subject, job.Body, ct);
    }
}

// Enqueue typed work
await orchestrator.EnqueueAsync(new EmailJob("user@example.com", "Hello", "..."), ct);
```

### Adapter for Legacy Code

```csharp
// For gradual migration, provide an adapter
public sealed class DelegateWorkHandler : IWorkHandler<Func<Task>>
{
    public async ValueTask HandleAsync(Func<Task> work, CancellationToken ct)
    {
        await work();
    }
}

// Usage
services.AddWorkOrchestrator<Func<Task>>()
    .WithHandler<DelegateWorkHandler>();
```

### ASP.NET Core Integration

```csharp
// Hosted service for lifecycle management
public static class HostedServiceExtensions
{
    public static IServiceCollection AddWorkOrchestratorHostedService<TWork>(
        this IServiceCollection services)
    {
        services.AddHostedService<WorkOrchestratorHostedService<TWork>>();
        return services;
    }
}

internal sealed class WorkOrchestratorHostedService<TWork> : IHostedService
{
    private readonly IWorkOrchestrator<TWork> _orchestrator;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask; // Workers start on construction
    public Task StopAsync(CancellationToken ct) => _orchestrator.StopAsync(ct);
}
```

---

## Testing Strategy

### Unit Tests (TUnit)

| Component | Test Coverage |
|-----------|---------------|
| `WorkOrchestrator<T>` | Enqueue/dequeue, worker lifecycle, backpressure |
| `AutoscalingEngine` | Watermark decisions, cooldown periods, hysteresis |
| `AutoscalingOrchestrator<T>` | Decorator behavior, metrics collection |
| `WorkerRegistry` | Worker lifecycle, state transitions |
| `WorkOrchestratorBuilder<T>` | Decorator ordering, configuration validation |

### Integration Tests

- End-to-end work processing with real handlers
- Autoscaling behavior under load
- Graceful shutdown with in-flight work
- Event stream consumption

### Performance Benchmarks (BenchmarkDotNet)

```csharp
[MemoryDiagnoser]
public class EnqueueBenchmarks
{
    [Benchmark]
    public async Task EnqueueAsync_SingleItem()
    {
        await _orchestrator.EnqueueAsync(_workItem, default);
    }

    [Benchmark]
    public void TryEnqueue_SingleItem()
    {
        _orchestrator.TryEnqueue(_workItem);
    }
}
```

**Target metrics:**
- `EnqueueAsync`: < 100ns, 0 allocations
- `TryEnqueue`: < 50ns, 0 allocations
- Worker throughput: > 1M items/sec single worker

---

## Open Questions

1. **Naming**: `Bifrost` vs `Bifrost.WorkOrchestrator` vs other?

2. **Generic event filtering**: Should `GetEventStreamAsync<TEvent>()` filter by type, or should we use a different pattern?

3. **Work prioritization**: Should we support priority queues as an optional feature? (Would require `PriorityChannel` implementation)

4. **Distributed scaling**: Future consideration for distributed worker pools (Redis-backed, etc.)?

5. **Cancellation semantics**: Should `EnqueueAsync` cancellation abort the enqueue, or just stop waiting for capacity?

---

## Implementation Phases

### Phase 1: Core Extraction
- Extract `WorkOrchestrator<T>` with typed work items
- Extract `IWorkHandler<T>` contract
- Basic `AddWorkOrchestrator<T>()` registration
- Unit tests for core functionality

### Phase 2: Autoscaling
- Extract `AutoscalingEngine`, `WorkerRegistry`, `WorkerMetrics`
- `WithAutoscaling()` builder extension
- `AutoscalingCoordinator` for circular dependency resolution
- Decorator implementation

### Phase 3: Resilience & Events
- `WithResilience()` with Polly integration
- `WithEventStream()` for pub/sub events
- Struct-based event types

### Phase 4: Observability
- `WithHealthChecks()` integration
- `WithOpenTelemetry()` metrics and tracing
- Documentation and samples

### Phase 5: Optimization
- BenchmarkDotNet performance validation
- Allocation analysis and optimization
- Object pooling where beneficial
