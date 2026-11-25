// =============================================================================
// <copyright file="DelegateHandlerExtensionsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;

using Bifrost.Core;
using Bifrost.DependencyInjection;
using Bifrost.Handlers;

using Microsoft.Extensions.DependencyInjection;

using TUnit.Core;

namespace Bifrost.Tests.DependencyInjection;

/// <summary>
/// Tests for the <c>WithTaskHandler</c> and <c>WithScopedHandler</c> registrations in
/// <see cref="HandlerExtensions"/>.
/// </summary>
/// <remarks>
/// Waits use <see cref="TaskCompletionSource"/> or <see cref="SemaphoreSlim"/> signals with a bounded
/// timeout instead of fixed sleeps.
/// </remarks>
[Property("Category", "Unit")]
public class DelegateHandlerExtensionsTests
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that a static async <see cref="Task"/> method binds to <c>WithTaskHandler</c> as a
    /// method group and runs for an enqueued item.
    /// </summary>
    [Test]
    public async Task WithTaskHandler_MethodGroup_CompilesAndRuns()
    {
        var services = CreateServices();
        var builder = services.AddWorkOrchestrator<Probe>(opts => opts.WorkerCount = 1);

        builder.WithTaskHandler(HandleProbeTaskAsync);
        builder.Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<Probe>>();
        var probe = new Probe();

        await orchestrator.EnqueueAsync(probe);
        var handled = await probe.Done.Task.WaitAsync(SignalTimeout);

        await Assert.That(handled).IsEqualTo("task");
        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Verifies that a <see cref="ValueTask"/> method binds to <c>WithScopedHandler</c> as a method
    /// group and runs for an enqueued item.
    /// </summary>
    [Test]
    public async Task WithScopedHandler_MethodGroup_CompilesAndRuns()
    {
        var services = CreateServices();
        var builder = services.AddWorkOrchestrator<Probe>(opts => opts.WorkerCount = 1);

        builder.WithScopedHandler(HandleProbeScopedAsync);
        builder.Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<Probe>>();
        var probe = new Probe();

        await orchestrator.EnqueueAsync(probe);
        var handled = await probe.Done.Task.WaitAsync(SignalTimeout);

        await Assert.That(handled).IsEqualTo("scoped");
        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Verifies that <c>WithTaskHandler</c> registers a singleton handler and records the lifetime.
    /// </summary>
    [Test]
    public async Task WithTaskHandler_RegistersSingleton()
    {
        var services = CreateServices();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        var result = builder.WithTaskHandler((work, ct) => Task.CompletedTask);

        var descriptor = services.Single(d => d.ServiceType == typeof(IWorkHandler<string>));
        await Assert.That(result).IsSameReferenceAs(builder);
        await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
        await Assert.That(builder.HandlerRegistered).IsTrue();
        await Assert.That(builder.HandlerLifetime).IsEqualTo(ServiceLifetime.Singleton);
    }

    /// <summary>
    /// Verifies that <c>WithScopedHandler</c> registers a scoped handler and records the scoped
    /// lifetime so that <c>Build</c> routes it through the scoped handler proxy.
    /// </summary>
    [Test]
    public async Task WithScopedHandler_RegistersScoped()
    {
        var services = CreateServices();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        var result = builder.WithScopedHandler((sp, work, ct) => ValueTask.CompletedTask);

        var descriptor = services.Single(d => d.ServiceType == typeof(IWorkHandler<string>));
        await Assert.That(result).IsSameReferenceAs(builder);
        await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Scoped);
        await Assert.That(builder.HandlerRegistered).IsTrue();
        await Assert.That(builder.HandlerLifetime).IsEqualTo(ServiceLifetime.Scoped);
    }

    /// <summary>
    /// Verifies that two items handled by <c>WithScopedHandler</c> each see a different scoped
    /// dependency and that each scope is disposed after its handler exits.
    /// </summary>
    [Test]
    public async Task WithScopedHandler_ResolvesFreshScopePerItem_AndDisposesAfterHandlerExits()
    {
        var tracker = new ScopeTracker();
        var services = CreateServices();
        services.AddSingleton(tracker);
        services.AddScoped<ScopedProbe>();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        builder.WithScopedHandler(async (sp, work, ct) =>
        {
            var probe = sp.GetRequiredService<ScopedProbe>();
            await Task.Yield();
            tracker.Record($"handled:{probe.Id}:{probe.IsDisposed}");
        });
        builder.Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        await orchestrator.EnqueueAsync("item-1");
        await orchestrator.EnqueueAsync("item-2");
        await tracker.WaitForDisposalsAsync(2, SignalTimeout);

        var events = tracker.Events.ToList();
        var handled = events.Where(e => e.StartsWith("handled:", StringComparison.Ordinal)).ToList();
        var disposed = events.Where(e => e.StartsWith("disposed:", StringComparison.Ordinal)).ToList();
        var ids = handled.Select(e => e.Split(':')[1]).ToList();

        await Assert.That(handled.Count).IsEqualTo(2);
        await Assert.That(ids.Distinct().Count()).IsEqualTo(2);
        await Assert.That(handled.All(e => e.EndsWith(":False", StringComparison.Ordinal))).IsTrue();
        await Assert.That(disposed.Count).IsEqualTo(2);
        foreach (var id in ids)
        {
            await Assert.That(events.IndexOf($"disposed:{id}"))
                .IsGreaterThan(events.FindIndex(e => e.StartsWith($"handled:{id}", StringComparison.Ordinal)));
        }

        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Verifies that the scope created for a <c>WithScopedHandler</c> call is disposed when the
    /// handler throws, and that the exception reaches the caller.
    /// </summary>
    [Test]
    public async Task WithScopedHandler_HandlerThrows_ScopeStillDisposed()
    {
        var tracker = new ScopeTracker();
        var services = CreateServices();
        services.AddSingleton(tracker);
        services.AddScoped<ScopedProbe>();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        builder.WithScopedHandler(async (sp, work, ct) =>
        {
            var probe = sp.GetRequiredService<ScopedProbe>();
            await Task.Yield();
            tracker.Record($"handled:{probe.Id}");
            throw new InvalidOperationException("boom");
        });
        await using var provider = services.BuildServiceProvider();
        var proxy = new ScopedHandlerProxy<string>(provider.GetRequiredService<IServiceScopeFactory>());

        await Assert.That(async () => await proxy.HandleAsync("item", CancellationToken.None))
            .Throws<InvalidOperationException>();
        await tracker.WaitForDisposalsAsync(1, SignalTimeout);

        var events = tracker.Events.ToList();
        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(events[0]).StartsWith("handled:");
        await Assert.That(events[1]).StartsWith("disposed:");
    }

    /// <summary>
    /// Verifies that a throwing <c>WithScopedHandler</c> delegate run by the orchestrator still
    /// disposes its scope.
    /// </summary>
    [Test]
    public async Task WithScopedHandler_HandlerThrowsInOrchestrator_ScopeStillDisposed()
    {
        var tracker = new ScopeTracker();
        var services = CreateServices();
        services.AddSingleton(tracker);
        services.AddScoped<ScopedProbe>();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        builder.WithScopedHandler((sp, work, ct) =>
        {
            _ = sp.GetRequiredService<ScopedProbe>();
            throw new InvalidOperationException("boom");
        });
        builder.Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        await orchestrator.EnqueueAsync("item");
        await tracker.WaitForDisposalsAsync(1, SignalTimeout);

        await Assert.That(tracker.Events.Count(e => e.StartsWith("disposed:", StringComparison.Ordinal)))
            .IsEqualTo(1);
        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Verifies that the <c>WithTaskHandler</c> delegate receives the token passed to
    /// <see cref="IWorkHandler{TWork}.HandleAsync"/>.
    /// </summary>
    [Test]
    public async Task WithTaskHandler_ReceivesHandleAsyncToken()
    {
        var services = CreateServices();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);
        CancellationToken received = default;
        builder.WithTaskHandler((work, ct) =>
        {
            received = ct;
            return Task.CompletedTask;
        });
        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IWorkHandler<string>>();
        using var cts = new CancellationTokenSource();

        await handler.HandleAsync("item", cts.Token);

        await Assert.That(received).IsEqualTo(cts.Token);
    }

    /// <summary>
    /// Verifies that the <c>WithScopedHandler</c> delegate receives the token passed to the scoped
    /// handler proxy's <see cref="IWorkHandler{TWork}.HandleAsync"/>.
    /// </summary>
    [Test]
    public async Task WithScopedHandler_ReceivesHandleAsyncToken()
    {
        var services = CreateServices();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);
        CancellationToken received = default;
        builder.WithScopedHandler((sp, work, ct) =>
        {
            received = ct;
            return ValueTask.CompletedTask;
        });
        await using var provider = services.BuildServiceProvider();
        var proxy = new ScopedHandlerProxy<string>(provider.GetRequiredService<IServiceScopeFactory>());
        using var cts = new CancellationTokenSource();

        await proxy.HandleAsync("item", cts.Token);

        await Assert.That(received).IsEqualTo(cts.Token);
    }

    /// <summary>
    /// Verifies that a running <c>WithTaskHandler</c> delegate observes cancellation when the
    /// orchestrator aborts.
    /// </summary>
    [Test]
    public async Task WithTaskHandler_OrchestratorAbort_CancelsHandler()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = CreateServices();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);
        builder.WithTaskHandler(async (work, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult();
                throw;
            }
        });
        builder.Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        await orchestrator.EnqueueAsync("item");
        await started.Task.WaitAsync(SignalTimeout);
        await orchestrator.AbortAsync();
        await canceled.Task.WaitAsync(SignalTimeout);

        await Assert.That(canceled.Task.IsCompletedSuccessfully).IsTrue();
        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Verifies that a running <c>WithScopedHandler</c> delegate observes cancellation when the
    /// orchestrator aborts, and that its scope is disposed afterwards.
    /// </summary>
    [Test]
    public async Task WithScopedHandler_OrchestratorAbort_CancelsHandler()
    {
        var tracker = new ScopeTracker();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = CreateServices();
        services.AddSingleton(tracker);
        services.AddScoped<ScopedProbe>();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);
        builder.WithScopedHandler(async (sp, work, ct) =>
        {
            _ = sp.GetRequiredService<ScopedProbe>();
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult();
                throw;
            }
        });
        builder.Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        await orchestrator.EnqueueAsync("item");
        await started.Task.WaitAsync(SignalTimeout);
        await orchestrator.AbortAsync();
        await canceled.Task.WaitAsync(SignalTimeout);
        await tracker.WaitForDisposalsAsync(1, SignalTimeout);

        await Assert.That(canceled.Task.IsCompletedSuccessfully).IsTrue();
        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Verifies that registering any two handlers from <c>WithHandler</c>, <c>WithTaskHandler</c> and
    /// <c>WithScopedHandler</c> on the same builder throws, and that the message names all three.
    /// </summary>
    /// <param name="first">The registration made first.</param>
    /// <param name="second">The registration made second.</param>
    [Test]
    [Arguments("value", "value")]
    [Arguments("value", "task")]
    [Arguments("value", "scoped")]
    [Arguments("task", "value")]
    [Arguments("task", "task")]
    [Arguments("task", "scoped")]
    [Arguments("scoped", "value")]
    [Arguments("scoped", "task")]
    [Arguments("scoped", "scoped")]
    public async Task DuplicateRegistration_AnyPair_ThrowsInvalidOperationException(string first, string second)
    {
        var services = CreateServices();
        var builder = services.AddWorkOrchestrator<string>();
        Register(builder, first);

        await Assert.That(() => Register(builder, second))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("WithHandler, WithTaskHandler, or WithScopedHandler");
    }

    /// <summary>
    /// Verifies that an existing <c>WithHandler</c> async lambda still binds to the
    /// <see cref="ValueTask"/> overload and registers the inline delegate handler.
    /// </summary>
    [Test]
    public async Task WithHandler_AsyncLambda_StillBindsToValueTaskOverload()
    {
        var services = CreateServices();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        builder.WithHandler(async (w, ct) => { await Task.Yield(); });

        var descriptor = services.Single(d => d.ServiceType == typeof(IWorkHandler<string>));
        await Assert.That(descriptor.ImplementationInstance).IsTypeOf<InlineDelegateWorkHandler<string>>();
    }

    /// <summary>
    /// Verifies that <c>WithTaskHandler</c> rejects a null builder.
    /// </summary>
    [Test]
    public async Task WithTaskHandler_NullBuilder_ThrowsArgumentNullException()
    {
        await Assert.That(() => HandlerExtensions.WithTaskHandler<string>(null!, (w, ct) => Task.CompletedTask))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that <c>WithTaskHandler</c> rejects a null delegate.
    /// </summary>
    [Test]
    public async Task WithTaskHandler_NullDelegate_ThrowsArgumentNullException()
    {
        var builder = CreateServices().AddWorkOrchestrator<string>();

        await Assert.That(() => builder.WithTaskHandler(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that <c>WithScopedHandler</c> rejects a null builder.
    /// </summary>
    [Test]
    public async Task WithScopedHandler_NullBuilder_ThrowsArgumentNullException()
    {
        await Assert.That(() =>
                HandlerExtensions.WithScopedHandler<string>(null!, (sp, w, ct) => ValueTask.CompletedTask))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that <c>WithScopedHandler</c> rejects a null delegate.
    /// </summary>
    [Test]
    public async Task WithScopedHandler_NullDelegate_ThrowsArgumentNullException()
    {
        var builder = CreateServices().AddWorkOrchestrator<string>();

        await Assert.That(() => builder.WithScopedHandler(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that a <c>WithTaskHandler</c> delegate returning a null <see cref="Task"/> fails with
    /// <see cref="InvalidOperationException"/> when the handler runs.
    /// </summary>
    [Test]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD114:Avoid returning a null Task", Justification = "The test checks how a null Task from the delegate is reported.")]
    public async Task WithTaskHandler_DelegateReturnsNullTask_ThrowsInvalidOperationException()
    {
        var services = CreateServices();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);
        builder.WithTaskHandler((work, ct) => null!);
        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IWorkHandler<string>>();

        await Assert.That(async () => await handler.HandleAsync("item", CancellationToken.None))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("null Task");
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    private static void Register(WorkOrchestratorBuilder<string> builder, string kind)
    {
        switch (kind)
        {
            case "value":
                builder.WithHandler((w, ct) => ValueTask.CompletedTask);
                break;
            case "task":
                builder.WithTaskHandler((w, ct) => Task.CompletedTask);
                break;
            case "scoped":
                builder.WithScopedHandler((sp, w, ct) => ValueTask.CompletedTask);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown registration kind.");
        }
    }

    private static async Task HandleProbeTaskAsync(Probe probe, CancellationToken ct)
    {
        await Task.Yield();
        probe.Done.TrySetResult("task");
    }

    private static ValueTask HandleProbeScopedAsync(IServiceProvider sp, Probe probe, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sp);
        probe.Done.TrySetResult("scoped");
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Work item that carries its own completion signal, so method-group handlers need no static state.
    /// </summary>
    public sealed class Probe
    {
        /// <summary>
        /// Gets the signal set by the handler with the name of the path that ran.
        /// </summary>
        public TaskCompletionSource<string> Done { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Records handler and disposal events for scoped probes and lets tests await disposals.
    /// </summary>
    private sealed class ScopeTracker
    {
        private readonly SemaphoreSlim _disposals = new(0);

        public ConcurrentQueue<string> Events { get; } = new();

        public void Record(string entry) => Events.Enqueue(entry);

        public void RecordDisposal(Guid id)
        {
            Events.Enqueue($"disposed:{id}");
            _disposals.Release();
        }

        public async Task WaitForDisposalsAsync(int count, TimeSpan timeout)
        {
            for (var i = 0; i < count; i++)
            {
                if (!await _disposals.WaitAsync(timeout))
                {
                    throw new TimeoutException($"Expected {count} scope disposals; saw {i}.");
                }
            }
        }
    }

    /// <summary>
    /// Scoped dependency that reports its own asynchronous disposal to the tracker.
    /// </summary>
    private sealed class ScopedProbe : IAsyncDisposable
    {
        private readonly ScopeTracker _tracker;

        public ScopedProbe(ScopeTracker tracker)
        {
            _tracker = tracker;
        }

        public Guid Id { get; } = Guid.NewGuid();

        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            _tracker.RecordDisposal(Id);
            return ValueTask.CompletedTask;
        }
    }
}
