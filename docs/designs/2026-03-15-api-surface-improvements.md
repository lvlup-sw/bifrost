# Design: API Surface Improvements for 0.4.0

## Problem Statement

During integration of Bifrost 0.3.5 into a production .NET 10 application (FHIR sync orchestration with EF Core, background services, and admin DLQ management), nine API surface gaps were identified that require workarounds. These range from critical missing builder ergonomics (handler registration) to medium-priority extensibility gaps (DLQ subscribers, handler decorators) to documentation clarifications.

Issue: [#14](https://github.com/lvlup-sw/bifrost/issues/14)

**Success criteria:**
- All 9 items from issue #14 addressed
- Zero breaking changes to existing public API
- 80%+ test coverage on new code
- All new builder methods chainable with existing `With*()` methods in any order

---

## Chosen Approach: Hybrid (Interface + Delegate)

Interfaces where they follow existing patterns (`IDeadLetterSubscriber<T>` mirrors `IWorkHandler<T>`), delegates where composition is the natural fit (handler decorators). Sync resilience documented as non-goal rather than implemented.

---

## Detailed Design

### 1. `.WithHandler<T>()` Builder Method (Critical)

**New file:** `src/Bifrost/DependencyInjection/HandlerExtensions.cs`

Three overloads following the pattern established by other builder extensions:

```csharp
public static class HandlerExtensions
{
    // Type-based registration with DI lifetime control
    public static WorkOrchestratorBuilder<TWork> WithHandler<TWork, THandler>(
        this WorkOrchestratorBuilder<TWork> builder,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where THandler : class, IWorkHandler<TWork>;

    // Factory-based registration for complex construction
    public static WorkOrchestratorBuilder<TWork> WithHandler<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Func<IServiceProvider, IWorkHandler<TWork>> factory,
        ServiceLifetime lifetime = ServiceLifetime.Singleton);

    // Delegate shorthand (registers as singleton, wraps in DelegateWorkHandler)
    public static WorkOrchestratorBuilder<TWork> WithHandler<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Func<TWork, CancellationToken, ValueTask> handler);
}
```

**Implementation:**

Each overload registers `IWorkHandler<TWork>` in the service collection at the specified lifetime. The type-based overload uses `services.Add(ServiceDescriptor.Describe(typeof(IWorkHandler<TWork>), typeof(THandler), lifetime))`. The delegate overload wraps in an internal `InlineDelegateWorkHandler<TWork>`.

**Validation:**
- Builder tracks whether `WithHandler` was called via an internal `bool HandlerRegistered` flag
- `Build()` validates that either `WithHandler` was called OR `IWorkHandler<TWork>` is already registered in the service collection (backward compatible)
- Calling `WithHandler` twice throws `InvalidOperationException`

**Usage:**

```csharp
services.AddWorkOrchestrator<FhirSyncJob>(opts => { ... })
    .WithHandler<FhirSyncJob, FhirSyncWorkHandler>(ServiceLifetime.Scoped)
    .WithDeadLetterQueue()
    .Build();

// Or with delegate
services.AddWorkOrchestrator<FhirSyncJob>(opts => { ... })
    .WithHandler<FhirSyncJob>(async (work, ct) =>
    {
        await ProcessAsync(work, ct);
    })
    .Build();
```

---

### 2. Scoped Handler Resolution Per Work Item (Critical)

**New file:** `src/Bifrost/Handlers/ScopedHandlerProxy.cs`

When `WithHandler<TWork, THandler>(ServiceLifetime.Scoped)` is used, the builder must create a DI scope per work item. The handler cannot be resolved once at startup — it must be resolved per-invocation within a scope.

**Design:**

```csharp
internal sealed class ScopedHandlerProxy<TWork> : IWorkHandler<TWork>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ScopedHandlerProxy(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async ValueTask HandleAsync(TWork work, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IWorkHandler<TWork>>();
        await handler.HandleAsync(work, ct).ConfigureAwait(false);
    }
}
```

**Integration with Build():**

The builder tracks handler lifetime via an internal `ServiceLifetime? HandlerLifetime` property. In `Build()`:

```csharp
Services.AddSingleton<IWorkOrchestrator<TWork>>(sp =>
{
    IWorkHandler<TWork> handler;

    if (HandlerLifetime == ServiceLifetime.Scoped)
    {
        // Scope-per-item: use proxy instead of direct resolution
        handler = new ScopedHandlerProxy<TWork>(sp.GetRequiredService<IServiceScopeFactory>());
    }
    else
    {
        // Singleton/Transient: resolve once at startup (existing behavior)
        handler = sp.GetRequiredService<IWorkHandler<TWork>>();
    }

    // Apply handler decorators (DLQ retry, completion tracking, etc.)
    foreach (var registration in orderedHandlerDecorators)
    {
        handler = registration.Factory(sp, handler);
    }

    // ... rest unchanged
});
```

**Decorator interaction:** Handler decorators wrap the `ScopedHandlerProxy`. This is correct:
- `DeadLetterHandler` wraps proxy → each retry creates a fresh scope (correct for DbContext)
- `CompletionTrackingHandler` wraps DLQ handler → timing is per-item including scope creation

**DI validation:** When `ServiceLifetime.Scoped` is used, `WithHandler` registers the handler type as scoped. The proxy is resolved from the root provider (it's a singleton that creates scopes), avoiding the captive dependency problem.

---

### 3. Public DLQ Subscriber API (High)

**New file (interface):** `src/Bifrost.Core/DeadLetter/IDeadLetterSubscriber.cs`

```csharp
/// <summary>
/// Subscriber that reacts to dead-lettered work items.
/// </summary>
public interface IDeadLetterSubscriber<TWork>
{
    /// <summary>
    /// Handles a dead-lettered work item.
    /// </summary>
    Task HandleAsync(DeadLetteredWork<TWork> item, CancellationToken ct);
}
```

**New file (extension):** `src/Bifrost/DependencyInjection/DeadLetterSubscriberExtensions.cs`

```csharp
public static class DeadLetterSubscriberExtensions
{
    // Interface-based subscriber (DI-integrated)
    public static WorkOrchestratorBuilder<TWork> WithDeadLetterSubscriber<TWork, TSubscriber>(
        this WorkOrchestratorBuilder<TWork> builder)
        where TSubscriber : class, IDeadLetterSubscriber<TWork>;

    // Callback subscriber (simple cases)
    public static WorkOrchestratorBuilder<TWork> WithDeadLetterSubscriber<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Func<DeadLetteredWork<TWork>, CancellationToken, Task> callback);
}
```

**Modified:** `DeadLetterNotifier<TWork>` changes from single-subscriber to multi-subscriber:

```csharp
internal sealed class DeadLetterNotifier<TWork>
{
    private readonly List<Func<WorkDeadLetteredEvent<TWork>, Task>> _callbacks = [];
    private readonly object _lock = new();

    public void Subscribe(Func<WorkDeadLetteredEvent<TWork>, Task> callback)
    {
        lock (_lock)
        {
            _callbacks.Add(callback);
        }
    }

    public void Notify(WorkDeadLetteredEvent<TWork> evt)
    {
        List<Func<WorkDeadLetteredEvent<TWork>, Task>> snapshot;
        lock (_lock)
        {
            snapshot = [.. _callbacks];
        }

        foreach (var callback in snapshot)
        {
            // Fire-and-forget to avoid blocking the handler loop.
            // Errors are logged but don't affect the main processing path.
            _ = NotifySafe(callback, evt);
        }
    }

    private async Task NotifySafe(
        Func<WorkDeadLetteredEvent<TWork>, Task> callback,
        WorkDeadLetteredEvent<TWork> evt)
    {
        try
        {
            await callback(evt).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Subscriber errors must not propagate to the handler loop
        }
    }
}
```

**Registration flow:**

`WithDeadLetterSubscriber<T>()` registers `TSubscriber` in DI and adds a post-registration hook that, during `Build()`, resolves the subscriber and wires it into the notifier:

```csharp
builder.Services.AddSingleton<TSubscriber>();
builder.PostBuildActions.Add(sp =>
{
    var notifier = sp.GetRequiredService<DeadLetterNotifier<TWork>>();
    var subscriber = sp.GetRequiredService<TSubscriber>();
    notifier.Subscribe(async evt =>
    {
        var dlw = new DeadLetteredWork<TWork>(
            evt.Work, evt.Exception, evt.AttemptCount, evt.Timestamp, evt.CorrelationId);
        await subscriber.HandleAsync(dlw, CancellationToken.None);
    });
});
```

**Builder change:** Add `internal List<Action<IServiceProvider>> PostBuildActions { get; } = []` to `WorkOrchestratorBuilder<TWork>`. Execute these after the orchestrator factory resolves all services.

**Usage:**

```csharp
services.AddWorkOrchestrator<FhirSyncJob>(opts => { ... })
    .WithHandler<FhirSyncJob, FhirSyncWorkHandler>(ServiceLifetime.Scoped)
    .WithDeadLetterQueue(dlq => dlq.MaxRetries = 4)
    .WithDeadLetterSubscriber<FhirSyncJob, FhirSyncDeadLetterHandler>()
    .Build();

// Or simple callback
    .WithDeadLetterSubscriber<FhirSyncJob>(async (item, ct) =>
    {
        logger.LogCritical("Dead-lettered: {Work}", item.Work);
    })
```

---

### 4. Route `WorkDeadLetteredEvent` Through Event Stream (High)

**Problem:** `DeadLetterHandler` publishes `WorkDeadLetteredEvent` via `DeadLetterNotifier` only. If `WithEventStream()` is also configured, the event stream never sees DLQ events.

**Solution:** Add an `EventPublishCallback` to the builder that bridges the two systems.

**Modified:** `WorkOrchestratorBuilder<TWork>` gains:

```csharp
internal Action<IOrchestratorEvent>? EventPublishCallback { get; set; }
```

**Modified:** `EventStreamExtensions.WithEventStream()` sets the callback:

```csharp
// Existing captured reference
EventStreamOrchestrator<TWork>? eventStreamOrchestrator = null;

// NEW: Set builder-level publish callback for cross-cutting event routing
builder.EventPublishCallback = evt => eventStreamOrchestrator?.PublishToSubscribers(evt);
```

**Modified:** `DeadLetterHandler<TWork>` accepts optional publish callback:

```csharp
internal sealed class DeadLetterHandler<TWork> : IWorkHandler<TWork>
{
    private readonly Action<IOrchestratorEvent>? _eventPublishCallback;

    public DeadLetterHandler(
        IWorkHandler<TWork> inner,
        IDeadLetterQueue<TWork> dlq,
        DeadLetterNotifier<TWork> notifier,
        IOptions<DeadLetterQueueOptions> options,
        ILogger<DeadLetterHandler<TWork>> logger,
        Action<IOrchestratorEvent>? eventPublishCallback = null)
    {
        // ...
        _eventPublishCallback = eventPublishCallback;
    }

    public async ValueTask HandleAsync(TWork work, CancellationToken ct)
    {
        // ... existing retry logic ...

        // Dead-letter: notify subscribers AND publish to event stream
        _notifier.Notify(evt);
        _eventPublishCallback?.Invoke(evt);
    }
}
```

**Modified:** `DeadLetterQueueExtensions.WithDeadLetterQueue()` passes the callback:

```csharp
builder.HandlerDecorators.Add(new HandlerDecoratorRegistration<TWork>(
    Order: builder.HandlerDecorators.Count,
    Factory: (sp, handler) =>
        new DeadLetterHandler<TWork>(
            handler,
            sp.GetRequiredService<IDeadLetterQueue<TWork>>(),
            sp.GetRequiredService<DeadLetterNotifier<TWork>>(),
            sp.GetRequiredService<IOptions<DeadLetterQueueOptions>>(),
            sp.GetRequiredService<ILogger<DeadLetterHandler<TWork>>>(),
            builder.EventPublishCallback)));  // Late-bound: resolved at Build() time
```

**Order independence:** Because the factory closure captures `builder` (not the property value), `builder.EventPublishCallback` is read at factory execution time during `Build()`. Both `WithDeadLetterQueue()` and `WithEventStream()` can be called in any order.

**Result:** When both `.WithEventStream()` and `.WithDeadLetterQueue()` are configured, `WorkDeadLetteredEvent<TWork>` appears in the event stream alongside `WorkEnqueuedEvent` and `WorkCompletedEvent`. Consumers can subscribe:

```csharp
await foreach (var evt in orchestrator.GetEventStreamAsync<WorkDeadLetteredEvent<FhirSyncJob>>())
{
    // React to dead-lettered items via the event stream
}
```

---

### 5. Public `.WithHandlerDecorator()` Builder Method (Medium)

**New file:** `src/Bifrost/DependencyInjection/HandlerDecoratorExtensions.cs`

```csharp
public static class HandlerDecoratorExtensions
{
    /// <summary>
    /// Adds a custom handler decorator to the processing pipeline.
    /// </summary>
    /// <param name="builder">The builder to configure.</param>
    /// <param name="factory">Factory that wraps the inner handler.</param>
    /// <param name="order">
    /// Optional explicit order. If null, auto-assigns the next available order.
    /// Lower values are applied first (closer to the actual handler).
    /// Built-in decorator orders: DLQ = auto, CompletionTracking = auto.
    /// </param>
    public static WorkOrchestratorBuilder<TWork> WithHandlerDecorator<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Func<IServiceProvider, IWorkHandler<TWork>, IWorkHandler<TWork>> factory,
        int? order = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        var resolvedOrder = order ?? (builder.HandlerDecorators.Count == 0
            ? 0
            : builder.HandlerDecorators.Max(d => d.Order) + 1);

        builder.HandlerDecorators.Add(new HandlerDecoratorRegistration<TWork>(
            Order: resolvedOrder,
            Factory: factory));

        return builder;
    }
}
```

**Usage:**

```csharp
services.AddWorkOrchestrator<FhirSyncJob>(opts => { ... })
    .WithHandler<FhirSyncJob, FhirSyncWorkHandler>()
    .WithHandlerDecorator<FhirSyncJob>((sp, inner) =>
        new LoggingHandler<FhirSyncJob>(inner, sp.GetRequiredService<ILogger>()))
    .WithDeadLetterQueue()
    .Build();
```

**Note:** `HandlerDecoratorRegistration<TWork>` and `DecoratorRegistration<TWork>` remain `internal` — the public extension method is the approved API surface. This prevents consumers from bypassing validation.

---

### 6. Synchronous Methods Bypass Resilience Policies (Medium)

**Decision:** Document as explicit non-goal.

**Rationale:** `TryEnqueue()` and `TryRun()` write to a bounded `Channel<TWork>`. Channel writes are:
- CPU-bound (no I/O)
- Non-throwing on failure (return `false`)
- Sub-microsecond latency

Polly resilience policies target transient I/O failures (network timeouts, service unavailability). Wrapping a channel write with retry/timeout adds latency and allocation overhead to a hot path that doesn't experience transient failures.

**Modified:** Add XML documentation to `ResilientOrchestrator<TWork>`:

```csharp
/// <inheritdoc/>
/// <remarks>
/// <para>
/// This method is not wrapped with resilience policies. Synchronous channel writes
/// are CPU-bound operations that do not experience transient failures. Resilience
/// policies (retry, timeout, circuit breaker) target async I/O operations where
/// transient failures are expected.
/// </para>
/// <para>
/// If the channel is full, this method returns <c>false</c> immediately.
/// Use <see cref="EnqueueAsync"/> for backpressure with resilience protection.
/// </para>
/// </remarks>
public bool TryEnqueue(TWork work)
{
    return _inner.TryEnqueue(work);
}
```

Same documentation pattern applied to `TryRun()` and `Run()`.

---

### 7. Resilience vs DLQ Retry Interaction Documentation (Medium)

**No code changes.** Add XML documentation to key types explaining the two-layer retry model.

**On `DeadLetterQueueOptions`:**

```xml
/// <remarks>
/// <para><b>Retry model:</b></para>
/// <para>
/// Bifrost has two independent retry layers:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///     <b>Resilience layer</b> (<c>.WithResilience()</c>): Polly policies with exponential
///     backoff for transient failures on <b>enqueue operations</b>. Protects against
///     temporary channel/infrastructure failures.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>DLQ layer</b> (<c>.WithDeadLetterQueue()</c>): Immediate retries (no backoff)
///     on <b>handler execution</b> failures. After <see cref="MaxRetries"/> exhausted,
///     the work item is dead-lettered. Designed for persistent application-level failures,
///     not transient infrastructure issues.
///     </description>
///   </item>
/// </list>
/// <para>
/// Total attempts for a work item with both layers: the resilience layer retries
/// <b>enqueue</b> (getting work into the channel), then the DLQ layer retries
/// <b>handling</b> (processing the work item). They do not compound — each layer
/// operates on a different phase of the work lifecycle.
/// </para>
/// </remarks>
```

**On `ResiliencySettings`:**

Add parallel documentation noting that resilience applies to enqueue, not handler execution.

---

### 8. Clarify `MaxRetries` Semantics (Low)

**No rename.** Renaming `MaxRetries` to `MaxRetryAttempts` would be a breaking change. Instead, enhance XML documentation.

**Modified:** `DeadLetterQueueOptions.MaxRetries`:

```csharp
/// <summary>
/// Gets or sets the maximum number of retry attempts before dead-lettering.
/// </summary>
/// <value>The maximum retry count. Default is 3.</value>
/// <remarks>
/// <para>
/// This is the number of <b>retries after the initial attempt</b>. Total processing
/// attempts = 1 (initial) + MaxRetries.
/// </para>
/// <para>
/// Examples:
/// <list type="bullet">
///   <item><description><c>MaxRetries = 0</c>: 1 attempt total, dead-letter on first failure</description></item>
///   <item><description><c>MaxRetries = 3</c> (default): 4 attempts total</description></item>
///   <item><description><c>MaxRetries = 100</c>: 101 attempts total</description></item>
/// </list>
/// </para>
/// <para>
/// Retries are immediate (no backoff). For transient failure handling with
/// exponential backoff, use <c>.WithResilience()</c> instead.
/// </para>
/// </remarks>
[Range(0, 100)]
public int MaxRetries { get; set; } = 3;
```

---

### 9. `DrainAsync()` for Graceful Shutdown (Low)

**Modified:** `IWorkOrchestrator<TWork>` gains a new method:

```csharp
/// <summary>
/// Drains the orchestrator by processing all remaining queued items
/// without accepting new work.
/// </summary>
/// <param name="ct">Cancellation token to abort the drain operation.</param>
/// <returns>A task that completes when all queued items have been processed.</returns>
/// <remarks>
/// <para>
/// Unlike <see cref="StopAsync"/>, which cancels workers immediately,
/// <c>DrainAsync</c> completes the channel writer (preventing new enqueues)
/// and waits for workers to finish processing all remaining items naturally.
/// </para>
/// <para>
/// This is useful for zero-downtime deployments where in-flight work should
/// complete before the host shuts down.
/// </para>
/// <para>
/// After <c>DrainAsync</c> completes:
/// <list type="bullet">
///   <item><description><see cref="EnqueueAsync"/> will throw <see cref="ChannelClosedException"/></description></item>
///   <item><description><see cref="TryEnqueue"/> will return <c>false</c></description></item>
///   <item><description><see cref="PendingCount"/> will be 0</description></item>
/// </list>
/// </para>
/// </remarks>
Task DrainAsync(CancellationToken ct = default);
```

**Implementation in `WorkOrchestrator<TWork>`:**

```csharp
public async Task DrainAsync(CancellationToken ct = default)
{
    // Stop accepting new work
    _channel.Writer.TryComplete();

    // Wait for workers to finish processing remaining items
    // (workers exit naturally when the channel reader completes)
    await Task.WhenAll(_workers).WaitAsync(ct).ConfigureAwait(false);
}
```

**Decorator implementations:** Each decorator forwards to the inner orchestrator. Example for `ResilientOrchestrator<TWork>`:

```csharp
public Task DrainAsync(CancellationToken ct = default)
    => _inner.DrainAsync(ct);
```

`EventStreamOrchestrator<TWork>` additionally completes subscriber channels after drain:

```csharp
public async Task DrainAsync(CancellationToken ct = default)
{
    await _inner.DrainAsync(ct).ConfigureAwait(false);

    foreach (var subscriber in _eventSubscribers.Values)
    {
        subscriber.Writer.TryComplete();
    }
}
```

---

## New and Modified Files Summary

### New Files

| File | Description |
|------|-------------|
| `src/Bifrost/DependencyInjection/HandlerExtensions.cs` | `.WithHandler<T>()` overloads |
| `src/Bifrost/Handlers/ScopedHandlerProxy.cs` | Scope-per-item handler resolution |
| `src/Bifrost.Core/DeadLetter/IDeadLetterSubscriber.cs` | DLQ subscriber interface |
| `src/Bifrost/DependencyInjection/DeadLetterSubscriberExtensions.cs` | `.WithDeadLetterSubscriber<T>()` |
| `src/Bifrost/DependencyInjection/HandlerDecoratorExtensions.cs` | `.WithHandlerDecorator()` |
| `src/Bifrost/Handlers/InlineDelegateWorkHandler.cs` | Handler for `WithHandler(Func<...>)` delegate overload |

### Modified Files

| File | Changes |
|------|---------|
| `src/Bifrost/DependencyInjection/WorkOrchestratorBuilder.cs` | Add `HandlerLifetime`, `HandlerRegistered`, `EventPublishCallback`, `PostBuildActions` properties. Modify `Build()` for scoped resolution and post-build hooks. |
| `src/Bifrost/DeadLetter/DeadLetterNotifier.cs` | Multi-subscriber support (replace single callback with list) |
| `src/Bifrost/DeadLetter/DeadLetterHandler.cs` | Accept optional `Action<IOrchestratorEvent>?` publish callback for event stream routing |
| `src/Bifrost/DependencyInjection/DeadLetterQueueExtensions.cs` | Pass `builder.EventPublishCallback` to `DeadLetterHandler` factory |
| `src/Bifrost/DependencyInjection/EventStreamExtensions.cs` | Set `builder.EventPublishCallback` |
| `src/Bifrost.Core/IWorkOrchestrator.cs` | Add `DrainAsync()` method |
| `src/Bifrost/WorkOrchestrator.cs` | Implement `DrainAsync()` |
| `src/Bifrost/Decorators/EventStreamOrchestrator.cs` | Implement `DrainAsync()` with subscriber cleanup |
| `src/Bifrost.Resilience/ResilientOrchestrator.cs` | Implement `DrainAsync()` passthrough, add XML docs for sync bypass |
| `src/Bifrost.Core/DeadLetter/DeadLetterQueueOptions.cs` | Enhanced XML docs for `MaxRetries` semantics and retry layer interaction |

### Decorator-implementing files that need `DrainAsync()` passthrough

| File | Implementation |
|------|---------------|
| `src/Bifrost/Decorators/AutoscalingOrchestrator.cs` | Forward to inner, stop autoscaling engine |

---

## Backward Compatibility

All changes are additive:

1. **`WithHandler<T>()`** — Optional. Existing pattern of manually registering `IWorkHandler<TWork>` in DI continues to work. `Build()` falls back to `sp.GetRequiredService<IWorkHandler<TWork>>()` when `WithHandler` was not called.

2. **Scoped resolution** — Opt-in only when `ServiceLifetime.Scoped` is explicitly passed. Default remains `Singleton` (existing behavior).

3. **DLQ subscriber** — Additive. Existing `DeadLetterNotifier` internal API is not public. Multi-subscriber is backward compatible because the notifier currently has at most one subscriber (the event stream bridge, if configured).

4. **Event stream DLQ routing** — Automatic when both `WithEventStream()` and `WithDeadLetterQueue()` are configured. No behavior change when only one is used.

5. **Handler decorators** — New public API, existing internal mechanism unchanged.

6. **DrainAsync()** — New interface method. This is the only potentially breaking change for consumers implementing `IWorkOrchestrator<TWork>` directly. However, this interface is not designed for external implementation (all implementations are internal/sealed decorators). Any external implementors can add a no-op implementation.

---

## Testing Strategy

### New Test Classes

| Test Class | Coverage Target |
|------------|----------------|
| `HandlerExtensionsTests` | `WithHandler` type/factory/delegate overloads, lifetime validation, double-registration error |
| `ScopedHandlerProxyTests` | Scope creation per `HandleAsync`, disposal, concurrent scope isolation |
| `ScopedHandlerIntegrationTests` | End-to-end: scoped DbContext-like handler, verify fresh instance per work item |
| `DeadLetterSubscriberTests` | Interface subscriber invocation, callback subscriber, multiple subscribers, error isolation |
| `DeadLetterEventStreamIntegrationTests` | `WorkDeadLetteredEvent` appears in event stream when both DLQ and event stream configured |
| `HandlerDecoratorExtensionsTests` | Custom decorator registration, ordering, composition with built-in decorators |
| `DrainAsyncTests` | Drain completes pending items, rejects new enqueues, works with decorators |
| `DrainAsyncIntegrationTests` | Drain with active workers, DLQ interaction, event stream cleanup |

### Existing Tests to Update

- Builder tests: Verify backward compatibility (no `WithHandler` still works)
- DLQ tests: Verify multi-subscriber notifier doesn't break existing single-subscriber behavior
- Event stream tests: Verify `WorkDeadLetteredEvent` presence when DLQ configured

---

## Open Questions

None. All design decisions resolved during brainstorming.
