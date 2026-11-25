// =============================================================================
// <copyright file="AdmissionResilienceExtensionsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.DependencyInjection;
using Bifrost.Resilience;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Polly.Timeout;

using TUnit.Core;

namespace Bifrost.Tests.Resilience;

/// <summary>
/// Tests for <see cref="ResilienceExtensions.WithAdmissionResilience{TWork}"/> and for repeated
/// calls to <see cref="ResilienceExtensions.WithResilience{TWork}"/>.
/// </summary>
/// <remarks>
/// Some tests put a substitute orchestrator under the resilience decorator through an internal
/// decorator registration at order 10. The substitute stands in for the inner orchestrator, so the
/// tests can count how often the admission policy calls it.
/// </remarks>
[Property("Category", "Unit")]
public class AdmissionResilienceExtensionsTests
{
    private const int ResilienceOrder = 25;

    /// <summary>
    /// WithAdmissionResilience rejects a null builder.
    /// </summary>
    [Test]
    public async Task WithAdmissionResilience_NullBuilder_Throws()
    {
        await Assert.That(() => ResilienceExtensions.WithAdmissionResilience<string>(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// WithAdmissionResilience returns the same builder for chaining.
    /// </summary>
    [Test]
    public async Task WithAdmissionResilience_ValidBuilder_ReturnsBuilder()
    {
        var builder = CreateServices().AddWorkOrchestrator<string>();

        var result = builder.WithAdmissionResilience();

        await Assert.That(result).IsEqualTo(builder);
    }

    /// <summary>
    /// WithAdmissionResilience adds the resilience decorator as the outer orchestrator.
    /// </summary>
    [Test]
    public async Task WithAdmissionResilience_RegistersResilientOrchestratorDecorator()
    {
        var services = CreateServices();
        var builder = services.AddWorkOrchestrator<string>();

        builder.WithAdmissionResilience();
        builder.Build();
        await using var provider = services.BuildServiceProvider();

        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        await Assert.That(orchestrator).IsAssignableTo<ResilientOrchestrator<string>>();
        await Assert.That(builder.Decorators.Count(d => d.Order == ResilienceOrder)).IsEqualTo(1);
    }

    /// <summary>
    /// EnqueueAsync goes through the admission policy: a transient failure of the inner
    /// EnqueueAsync is retried and the retry is accepted.
    /// </summary>
    [Test]
    public async Task WithAdmissionResilience_EnqueueAsync_RetriesTransientInnerFailure()
    {
        var flaky = Substitute.For<IWorkOrchestrator<string>>();
        flaky.EnqueueAsync(Arg.Any<string>(), Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new TimeoutException("transient"),
                _ => new ValueTask<EnqueueResult>(EnqueueResult.Accepted));

        await using var host = BuildWithInner(
            flaky,
            builder => builder.WithAdmissionResilience(FastRetry));
        var orchestrator = host.Provider.GetRequiredService<IWorkOrchestrator<string>>();

        var result = await orchestrator.EnqueueAsync("work");

        await Assert.That(result.IsAccepted).IsTrue();
        await Assert.That(CountCalls(flaky, nameof(IWorkOrchestrator<string>.EnqueueAsync))).IsEqualTo(2);
    }

    /// <summary>
    /// A rejection returned as an <see cref="EnqueueResult"/> is not an exception, so the
    /// admission policy returns it without a retry.
    /// </summary>
    [Test]
    public async Task WithAdmissionResilience_EnqueueAsyncRejectedResult_IsNotRetried()
    {
        var rejecting = Substitute.For<IWorkOrchestrator<string>>();
        rejecting.EnqueueAsync(Arg.Any<string>(), Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<EnqueueResult>(EnqueueResult.Rejected(RejectionReason.CapacityExceeded)));

        await using var host = BuildWithInner(
            rejecting,
            builder => builder.WithAdmissionResilience(FastRetry));
        var orchestrator = host.Provider.GetRequiredService<IWorkOrchestrator<string>>();

        var result = await orchestrator.EnqueueAsync("work");

        await Assert.That(result.IsAccepted).IsFalse();
        await Assert.That(result.Reason).IsEqualTo(RejectionReason.CapacityExceeded);
        await Assert.That(CountCalls(rejecting, nameof(IWorkOrchestrator<string>.EnqueueAsync))).IsEqualTo(1);
    }

    /// <summary>
    /// The synchronous Enqueue does not go through the admission policy, so a transient
    /// exception propagates after one call.
    /// </summary>
    [Test]
    public async Task WithAdmissionResilience_SynchronousEnqueue_BypassesPolicy()
    {
        var throwing = Substitute.For<IWorkOrchestrator<string>>();
        throwing.Enqueue(Arg.Any<string>(), Arg.Any<WorkEnqueueOptions>())
            .Returns(_ => throw new TimeoutException("transient"));

        await using var host = BuildWithInner(
            throwing,
            builder => builder.WithAdmissionResilience(FastRetry));
        var orchestrator = host.Provider.GetRequiredService<IWorkOrchestrator<string>>();

        await Assert.That(() => orchestrator.Enqueue("work")).Throws<TimeoutException>();
        await Assert.That(CountCalls(throwing, nameof(IWorkOrchestrator<string>.Enqueue))).IsEqualTo(1);
    }

    /// <summary>
    /// With a real orchestrator in <see cref="QueueFullMode.Wait"/>, an EnqueueAsync that waits on a
    /// full queue is cut off by the per-attempt timeout, the timed-out attempt is retried, and the
    /// caller gets <see cref="TimeoutRejectedException"/> after the retries run out.
    /// </summary>
    /// <remarks>
    /// The orchestrator has one worker and capacity 1. The sentinel item parks the worker inside a
    /// gated handler and the next item fills the queue, so the third EnqueueAsync must wait for
    /// space. A pass-through substitute at order 10 forwards EnqueueAsync to the real orchestrator
    /// and counts the attempts the admission policy makes. The outer <c>WaitAsync</c> bound turns a
    /// missing timeout policy into a <see cref="TimeoutException"/> instead of a hang.
    /// </remarks>
    [Test]
    public async Task WithAdmissionResilience_WaitModeFullQueue_TimesOutEachAttemptAndRetries()
    {
        var handler = new GateHandler();
        var services = CreateServices(handler);
        IWorkOrchestrator<string>? real = null;
        var counting = Substitute.For<IWorkOrchestrator<string>>();
        counting.EnqueueAsync(Arg.Any<string>(), Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => real!.EnqueueAsync(
                call.ArgAt<string>(0),
                call.ArgAt<WorkEnqueueOptions>(1),
                call.ArgAt<CancellationToken>(2)));

        var builder = services.AddWorkOrchestrator<string>(options =>
        {
            options.Capacity = 1;
            options.WorkerCount = 1;
            options.FullMode = QueueFullMode.Wait;
        });
        builder.Decorators.Add(new DecoratorRegistration<string>(10, (_, inner) =>
        {
            real = inner;
            return counting;
        }));
        builder.WithAdmissionResilience(settings =>
        {
            settings.RetryCount = 1;
            settings.RetryIntervalSeconds = 0;
            settings.UseExponentialBackoff = false;
            settings.TimeoutIntervalSeconds = 1;
        });
        builder.Build();

        var provider = services.BuildServiceProvider();
        try
        {
            var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

            await Assert.That((await orchestrator.EnqueueAsync("sentinel")).IsAccepted).IsTrue();
            await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That((await orchestrator.EnqueueAsync("fill")).IsAccepted).IsTrue();
            var attemptsBefore = CountCalls(counting, nameof(IWorkOrchestrator<string>.EnqueueAsync));

            await Assert.That(async () => await orchestrator.EnqueueAsync("waits").AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(15)))
                .Throws<TimeoutRejectedException>();

            var attempts = CountCalls(counting, nameof(IWorkOrchestrator<string>.EnqueueAsync)) - attemptsBefore;
            await Assert.That(attempts).IsEqualTo(2);
        }
        finally
        {
            handler.Release();
            await provider.DisposeAsync();
            if (real is not null)
            {
                await real.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Any pair of calls to WithResilience and WithAdmissionResilience builds without a duplicate
    /// decorator order, adds one resilience decorator, and applies both configure actions.
    /// </summary>
    /// <param name="first">The method name used for the first call.</param>
    /// <param name="second">The method name used for the second call.</param>
    [Test]
    [Arguments("resilience", "resilience")]
    [Arguments("admission", "admission")]
    [Arguments("resilience", "admission")]
    [Arguments("admission", "resilience")]
    public async Task RepeatedCalls_AnyPair_AddOneDecoratorAndMergeConfiguration(string first, string second)
    {
        var services = CreateServices();
        var builder = services.AddWorkOrchestrator<string>();

        Register(builder, first, settings =>
        {
            settings.RetryCount = 7;
            settings.TimeoutIntervalSeconds = 9;
        });
        Register(builder, second, settings => settings.TimeoutIntervalSeconds = 11);

        await Assert.That(builder.Build).ThrowsNothing();
        await Assert.That(builder.Decorators.Count(d => d.Order == ResilienceOrder)).IsEqualTo(1);

        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var settings = provider.GetRequiredService<IOptionsMonitor<ResiliencySettings>>()
            .Get(WorkOptions.Name<string>());

        await Assert.That(orchestrator).IsAssignableTo<ResilientOrchestrator<string>>();
        await Assert.That(settings.RetryCount).IsEqualTo(7);
        await Assert.That(settings.TimeoutIntervalSeconds).IsEqualTo(11);
    }

    /// <summary>
    /// Two calls put exactly one resilience layer around the inner orchestrator: with one retry
    /// configured, an always-failing inner EnqueueAsync is called twice, not once per layer.
    /// </summary>
    [Test]
    public async Task RepeatedCalls_WrapInnerOrchestratorOnce()
    {
        var failing = Substitute.For<IWorkOrchestrator<string>>();
        failing.EnqueueAsync(Arg.Any<string>(), Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<EnqueueResult>>(_ => throw new TimeoutException("transient"));

        await using var host = BuildWithInner(
            failing,
            builder => builder
                .WithResilience(FastRetry)
                .WithAdmissionResilience(settings => settings.TimeoutIntervalSeconds = 20));
        var orchestrator = host.Provider.GetRequiredService<IWorkOrchestrator<string>>();

        await Assert.That(async () => await orchestrator.EnqueueAsync("work")).Throws<TimeoutException>();
        await Assert.That(CountCalls(failing, nameof(IWorkOrchestrator<string>.EnqueueAsync))).IsEqualTo(2);
    }

    private static void FastRetry(ResiliencySettings settings)
    {
        settings.RetryCount = 1;
        settings.RetryIntervalSeconds = 0;
        settings.UseExponentialBackoff = false;
        settings.TimeoutIntervalSeconds = 30;
    }

    private static void Register(
        WorkOrchestratorBuilder<string> builder,
        string method,
        Action<ResiliencySettings> configure)
    {
        if (method == "admission")
        {
            builder.WithAdmissionResilience(configure);
        }
        else
        {
            builder.WithResilience(configure);
        }
    }

    private static ServiceCollection CreateServices(IWorkHandler<string>? handler = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkHandler<string>>(handler ?? Substitute.For<IWorkHandler<string>>());
        services.AddSingleton<ILogger<WorkOrchestrator<string>>>(NullLogger<WorkOrchestrator<string>>.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services;
    }

    /// <summary>
    /// Builds a provider whose resilience decorator wraps <paramref name="inner"/> instead of the
    /// real orchestrator.
    /// </summary>
    /// <remarks>
    /// The real orchestrator that the order-10 decorator replaces is still created. The returned
    /// host disposes it after the provider.
    /// </remarks>
    private static InnerHost BuildWithInner(
        IWorkOrchestrator<string> inner,
        Action<WorkOrchestratorBuilder<string>> configure)
    {
        var services = CreateServices();
        var replaced = new List<IWorkOrchestrator<string>>();
        var builder = services.AddWorkOrchestrator<string>();
        builder.Decorators.Add(new DecoratorRegistration<string>(10, (_, real) =>
        {
            replaced.Add(real);
            return inner;
        }));
        configure(builder);
        builder.Build();
        return new InnerHost(services.BuildServiceProvider(), replaced);
    }

    private static int CountCalls(IWorkOrchestrator<string> substitute, string methodName)
        => substitute.ReceivedCalls().Count(call => call.GetMethodInfo().Name == methodName);

    /// <summary>
    /// A handler that parks every invocation until <see cref="Release"/> is called.
    /// </summary>
    private sealed class GateHandler : IWorkHandler<string>
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a task that completes when the first item enters the handler.</summary>
        public Task Started => _started.Task;

        /// <summary>Releases all current and future handler invocations.</summary>
        public void Release() => _gate.TrySetResult();

        /// <inheritdoc/>
        public async ValueTask HandleAsync(string work, CancellationToken ct)
        {
            _started.TrySetResult();
            await _gate.Task.WaitAsync(ct);
        }
    }

    /// <summary>
    /// Owns the provider and the real orchestrators that a test decorator replaced.
    /// </summary>
    private sealed class InnerHost(ServiceProvider provider, List<IWorkOrchestrator<string>> replaced) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            foreach (var orchestrator in replaced)
            {
                await orchestrator.DisposeAsync();
            }
        }
    }
}
