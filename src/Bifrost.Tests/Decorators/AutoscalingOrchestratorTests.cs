// =============================================================================
// <copyright file="AutoscalingOrchestratorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;
using Bifrost.Core;
using Bifrost.Decorators;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.Decorators;

/// <summary>
/// Tests for <see cref="AutoscalingOrchestrator{TWork}"/> decorator.
/// </summary>
[Property("Category", "Unit")]
public class AutoscalingOrchestratorTests
{
    private IWorkOrchestrator<string> _inner = null!;
    private IWorkerRegistry _registry = null!;
    private IWorkerMetrics _metrics = null!;

    /// <summary>
    /// Sets up test dependencies.
    /// </summary>
    [Before(Test)]
    public Task Setup()
    {
        _inner = Substitute.For<IWorkOrchestrator<string>>();
        _registry = Substitute.For<IWorkerRegistry>();
        _metrics = Substitute.For<IWorkerMetrics>();

        _inner.PendingCount.Returns(0);
        _inner.ActiveWorkers.Returns(2);
        _inner.Capacity.Returns(100);
        _registry.ActiveWorkerCount.Returns(0);
        _registry.IdleWorkerCount.Returns(0);

        return Task.CompletedTask;
    }

    private AutoscalingOrchestrator<string> CreateOrchestrator(
        AutoscalingOptions? options = null)
    {
        return new AutoscalingOrchestrator<string>(
            _inner,
            _registry,
            _metrics,
            Options.Create(options ?? new AutoscalingOptions()),
            NullLogger<AutoscalingOrchestrator<string>>.Instance);
    }

    /// <summary>
    /// Verifies EnqueueAsync tracks metrics.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_TracksMetrics()
    {
        var orchestrator = CreateOrchestrator();
        _inner.EnqueueAsync("work", Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>()).Returns(EnqueueResult.Accepted);

        var result = await orchestrator.EnqueueAsync("work");

        await Assert.That(result).IsEqualTo(EnqueueResult.Accepted);
        _metrics.Received(1).RecordEnqueue();
        await _inner.Received(1).EnqueueAsync("work", Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies EnqueueAsync does not track metrics when admission is rejected.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_Rejected_DoesNotTrackMetrics()
    {
        var orchestrator = CreateOrchestrator();
        _inner.EnqueueAsync("work", Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>())
            .Returns(EnqueueResult.Rejected(RejectionReason.Shutdown));

        var result = await orchestrator.EnqueueAsync("work");

        await Assert.That(result.IsAccepted).IsFalse();
        _metrics.DidNotReceive().RecordEnqueue();
    }

    /// <summary>
    /// Verifies TryEnqueue tracks metrics on success.
    /// </summary>
    [Test]
    public async Task TryEnqueue_Success_TracksMetrics()
    {
        var orchestrator = CreateOrchestrator();
        _inner.Enqueue("work").Returns(EnqueueResult.Accepted);

        var result = orchestrator.TryEnqueue("work");

        await Assert.That(result).IsTrue();
        _metrics.Received(1).RecordEnqueue();
    }

    /// <summary>
    /// Verifies TryEnqueue does not track metrics on failure.
    /// </summary>
    [Test]
    public async Task TryEnqueue_Failure_DoesNotTrackMetrics()
    {
        var orchestrator = CreateOrchestrator();
        _inner.Enqueue("work").Returns(EnqueueResult.Rejected(RejectionReason.CapacityExceeded));

        var result = orchestrator.TryEnqueue("work");

        await Assert.That(result).IsFalse();
        _metrics.DidNotReceive().RecordEnqueue();
    }

    /// <summary>
    /// Verifies PendingCount delegates to inner.
    /// </summary>
    [Test]
    public async Task PendingCount_DelegatesToInner()
    {
        _inner.PendingCount.Returns(42);
        var orchestrator = CreateOrchestrator();

        var result = orchestrator.PendingCount;

        await Assert.That(result).IsEqualTo(42);
    }

    /// <summary>
    /// Verifies ActiveWorkers delegates to inner.
    /// </summary>
    [Test]
    public async Task ActiveWorkers_DelegatesToInner()
    {
        _inner.ActiveWorkers.Returns(8);
        var orchestrator = CreateOrchestrator();

        var result = orchestrator.ActiveWorkers;

        await Assert.That(result).IsEqualTo(8);
    }

    /// <summary>
    /// Verifies Capacity delegates to inner.
    /// </summary>
    [Test]
    public async Task Capacity_DelegatesToInner()
    {
        _inner.Capacity.Returns(256);
        var orchestrator = CreateOrchestrator();

        var result = orchestrator.Capacity;

        await Assert.That(result).IsEqualTo(256);
    }

    /// <summary>
    /// Verifies the work class flows through to the inner orchestrator.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_ForwardsWorkClassToInner()
    {
        var orchestrator = CreateOrchestrator();
        _inner.EnqueueAsync("work", Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>()).Returns(EnqueueResult.Accepted);

        await orchestrator.EnqueueAsync("work", WorkClass.Interactive);

        await _inner.Received(1).EnqueueAsync("work", WorkClass.Interactive, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies StopAsync delegates to inner.
    /// </summary>
    [Test]
    public async Task StopAsync_DelegatesToInner()
    {
        var orchestrator = CreateOrchestrator();
        _inner.AbortAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await orchestrator.AbortAsync();

        await _inner.Received(1).AbortAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies DisposeAsync delegates to inner.
    /// </summary>
    [Test]
    public async Task DisposeAsync_DelegatesToInner()
    {
        var orchestrator = CreateOrchestrator();
        _inner.DisposeAsync().Returns(ValueTask.CompletedTask);

        await orchestrator.DisposeAsync();

        await _inner.Received(1).DisposeAsync();
    }

    /// <summary>
    /// Verifies constructor throws on null inner.
    /// </summary>
    [Test]
    public async Task Constructor_NullInner_Throws()
    {
        await Assert.That(() => new AutoscalingOrchestrator<string>(
                null!, _registry, _metrics,
                Options.Create(new AutoscalingOptions()),
                NullLogger<AutoscalingOrchestrator<string>>.Instance))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies constructor throws on null metrics.
    /// </summary>
    [Test]
    public async Task Constructor_NullMetrics_Throws()
    {
        await Assert.That(() => new AutoscalingOrchestrator<string>(
                _inner, _registry, null!,
                Options.Create(new AutoscalingOptions()),
                NullLogger<AutoscalingOrchestrator<string>>.Instance))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies decorator implements IWorkOrchestrator.
    /// </summary>
    [Test]
    public async Task AutoscalingOrchestrator_ImplementsIWorkOrchestrator()
    {
        var orchestrator = CreateOrchestrator();

        await Assert.That(orchestrator).IsAssignableTo<IWorkOrchestrator<string>>();
    }

    /// <summary>
    /// Verifies DrainAsync delegates to inner orchestrator.
    /// </summary>
    [Test]
    public async Task DrainAsync_ForwardsToInner()
    {
        var orchestrator = CreateOrchestrator();
        _inner.DrainAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await orchestrator.DrainAsync();

        await _inner.Received(1).DrainAsync(Arg.Any<CancellationToken>());
    }
}
