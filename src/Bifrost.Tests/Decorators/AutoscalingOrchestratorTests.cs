// =============================================================================
// <copyright file="AutoscalingOrchestratorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Threading.Channels;

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

        // Setup default property values
        _inner.PendingCount.Returns(0);
        _inner.ActiveWorkers.Returns(2);
        _inner.Capacity.Returns(100);
        _inner.Writer.Returns(Channel.CreateUnbounded<string>().Writer);
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
        // Arrange
        var orchestrator = CreateOrchestrator();
        _inner.EnqueueAsync("work", Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);

        // Act
        await orchestrator.EnqueueAsync("work").ConfigureAwait(false);

        // Assert
        _metrics.Received(1).RecordEnqueue();
        await _inner.Received(1).EnqueueAsync("work", Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies TryEnqueue tracks metrics on success.
    /// </summary>
    [Test]
    public async Task TryEnqueue_Success_TracksMetrics()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        _inner.TryEnqueue("work").Returns(true);

        // Act
        var result = orchestrator.TryEnqueue("work");

        // Assert
        await Assert.That(result).IsTrue();
        _metrics.Received(1).RecordEnqueue();
    }

    /// <summary>
    /// Verifies TryEnqueue does not track metrics on failure.
    /// </summary>
    [Test]
    public async Task TryEnqueue_Failure_DoesNotTrackMetrics()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        _inner.TryEnqueue("work").Returns(false);

        // Act
        var result = orchestrator.TryEnqueue("work");

        // Assert
        await Assert.That(result).IsFalse();
        _metrics.DidNotReceive().RecordEnqueue();
    }

    /// <summary>
    /// Verifies PendingCount delegates to inner.
    /// </summary>
    [Test]
    public async Task PendingCount_DelegatesToInner()
    {
        // Arrange
        _inner.PendingCount.Returns(42);
        var orchestrator = CreateOrchestrator();

        // Act
        var result = orchestrator.PendingCount;

        // Assert
        await Assert.That(result).IsEqualTo(42);
    }

    /// <summary>
    /// Verifies ActiveWorkers delegates to inner.
    /// </summary>
    [Test]
    public async Task ActiveWorkers_DelegatesToInner()
    {
        // Arrange
        _inner.ActiveWorkers.Returns(8);
        var orchestrator = CreateOrchestrator();

        // Act
        var result = orchestrator.ActiveWorkers;

        // Assert
        await Assert.That(result).IsEqualTo(8);
    }

    /// <summary>
    /// Verifies Capacity delegates to inner.
    /// </summary>
    [Test]
    public async Task Capacity_DelegatesToInner()
    {
        // Arrange
        _inner.Capacity.Returns(256);
        var orchestrator = CreateOrchestrator();

        // Act
        var result = orchestrator.Capacity;

        // Assert
        await Assert.That(result).IsEqualTo(256);
    }

    /// <summary>
    /// Verifies Writer delegates to inner.
    /// </summary>
    [Test]
    public async Task Writer_DelegatesToInner()
    {
        // Arrange
        var expectedWriter = Channel.CreateUnbounded<string>().Writer;
        _inner.Writer.Returns(expectedWriter);
        var orchestrator = CreateOrchestrator();

        // Act
        var result = orchestrator.Writer;

        // Assert
        await Assert.That(result).IsEqualTo(expectedWriter);
    }

    /// <summary>
    /// Verifies StopAsync delegates to inner.
    /// </summary>
    [Test]
    public async Task StopAsync_DelegatesToInner()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        _inner.StopAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        // Act
        await orchestrator.StopAsync().ConfigureAwait(false);

        // Assert
        await _inner.Received(1).StopAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies DisposeAsync delegates to inner.
    /// </summary>
    [Test]
    public async Task DisposeAsync_DelegatesToInner()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        _inner.DisposeAsync().Returns(ValueTask.CompletedTask);

        // Act
        await orchestrator.DisposeAsync().ConfigureAwait(false);

        // Assert
        await _inner.Received(1).DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies constructor throws on null inner.
    /// </summary>
    [Test]
    public async Task Constructor_NullInner_Throws()
    {
        // Act & Assert
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
        // Act & Assert
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
        // Arrange
        var orchestrator = CreateOrchestrator();

        // Assert
        await Assert.That(orchestrator).IsAssignableTo<IWorkOrchestrator<string>>();
    }
}
