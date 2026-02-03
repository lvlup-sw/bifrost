// =============================================================================
// <copyright file="AutoscalingOrchestratorFullTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Threading.Channels;
using Bifrost.Autoscaling;
using Bifrost.Core;
using Bifrost.Decorators;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TUnit.Core;

namespace Bifrost.Tests.Decorators;

/// <summary>
/// Tests for <see cref="AutoscalingOrchestrator{TWork}"/> full feature implementation.
/// </summary>
[Property("Category", "Unit")]
public class AutoscalingOrchestratorFullTests
{
    private IWorkOrchestrator<string> _inner = null!;
    private IWorkerRegistry _registry = null!;
    private IWorkerMetrics _metrics = null!;
    private AutoscalingOptions _options = null!;
    private ILogger<AutoscalingOrchestrator<string>> _logger = null!;

    /// <summary>
    /// Sets up test dependencies.
    /// </summary>
    [Before(Test)]
    public Task Setup()
    {
        _inner = Substitute.For<IWorkOrchestrator<string>>();
        _registry = Substitute.For<IWorkerRegistry>();
        _metrics = Substitute.For<IWorkerMetrics>();
        _options = new AutoscalingOptions { Enabled = true };
        _logger = NullLogger<AutoscalingOrchestrator<string>>.Instance;

        // Setup default property values
        _inner.PendingCount.Returns(0);
        _inner.ActiveWorkers.Returns(2);
        _inner.Capacity.Returns(100);
        _inner.Writer.Returns(Channel.CreateUnbounded<string>().Writer);
        _registry.ActiveWorkerCount.Returns(0);
        _registry.IdleWorkerCount.Returns(0);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies EnqueueAsync records metrics when enabled.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_TracksMetrics()
    {
        // Arrange
        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);
        _inner.EnqueueAsync("work", Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);

        // Act
        await orchestrator.EnqueueAsync("work").ConfigureAwait(false);

        // Assert
        _metrics.Received(1).RecordEnqueue();
        await _inner.Received(1).EnqueueAsync("work", Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies RequestScaleUpAsync creates state-aware workers with busy/idle callbacks.
    /// </summary>
    [Test]
    public async Task RequestScaleUpAsync_CreatesStateAwareWorkers()
    {
        // Arrange
        var workerInfo = new WorkerInfo("test-worker");
        _registry.CreateWorkerAsync(
                Arg.Any<string>(),
                Arg.Any<Func<string, CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(workerInfo));

        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);

        // Act
        await orchestrator.RequestScaleUpAsync(2).ConfigureAwait(false);

        // Assert - Two workers should be created
        await _registry.Received(2).CreateWorkerAsync(
            Arg.Is<string>(s => s.StartsWith("AutoScale-", StringComparison.Ordinal)),
            Arg.Any<Func<string, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies RequestScaleDownAsync respects idle worker count.
    /// </summary>
    [Test]
    public async Task RequestScaleDownAsync_RespectsIdleWorkerCount()
    {
        // Arrange
        _registry.IdleWorkerCount.Returns(3);
        _registry.RequestMultipleWorkerStop(Arg.Any<int>()).Returns(3);

        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);

        // Act
        await orchestrator.RequestScaleDownAsync(5).ConfigureAwait(false);

        // Assert - Should only stop 3 (the idle count)
        _registry.Received(1).RequestMultipleWorkerStop(3);
    }

    /// <summary>
    /// Verifies ActiveWorkers includes registry workers.
    /// </summary>
    [Test]
    public async Task ActiveWorkers_IncludesRegistryWorkers()
    {
        // Arrange
        _inner.ActiveWorkers.Returns(5);
        _registry.ActiveWorkerCount.Returns(3);

        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);

        // Act
        var result = orchestrator.ActiveWorkers;

        // Assert
        await Assert.That(result).IsEqualTo(8);
    }

    /// <summary>
    /// Verifies constructor throws on null inner orchestrator.
    /// </summary>
    [Test]
    public async Task Constructor_NullInner_Throws()
    {
        // Act & Assert
        await Assert.That(() => new AutoscalingOrchestrator<string>(
                null!, _registry, _metrics, _options, _logger))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies constructor throws on null registry.
    /// </summary>
    [Test]
    public async Task Constructor_NullRegistry_Throws()
    {
        // Act & Assert
        await Assert.That(() => new AutoscalingOrchestrator<string>(
                _inner, null!, _metrics, _options, _logger))
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
                _inner, _registry, null!, _options, _logger))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies constructor throws on null options.
    /// </summary>
    [Test]
    public async Task Constructor_NullOptions_Throws()
    {
        // Act & Assert
        await Assert.That(() => new AutoscalingOrchestrator<string>(
                _inner, _registry, _metrics, null!, _logger))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies constructor throws on null logger.
    /// </summary>
    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        // Act & Assert
        await Assert.That(() => new AutoscalingOrchestrator<string>(
                _inner, _registry, _metrics, _options, null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies TryEnqueue records metrics on success.
    /// </summary>
    [Test]
    public async Task TryEnqueue_Success_TracksMetrics()
    {
        // Arrange
        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);
        _inner.TryEnqueue("work").Returns(true);

        // Act
        var result = orchestrator.TryEnqueue("work");

        // Assert
        await Assert.That(result).IsTrue();
        _metrics.Received(1).RecordEnqueue();
    }

    /// <summary>
    /// Verifies TryEnqueue does not record metrics on failure.
    /// </summary>
    [Test]
    public async Task TryEnqueue_Failure_DoesNotTrackMetrics()
    {
        // Arrange
        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);
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
        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);

        // Act
        var result = orchestrator.PendingCount;

        // Assert
        await Assert.That(result).IsEqualTo(42);
    }

    /// <summary>
    /// Verifies Capacity delegates to inner.
    /// </summary>
    [Test]
    public async Task Capacity_DelegatesToInner()
    {
        // Arrange
        _inner.Capacity.Returns(256);
        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);

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
        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);

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
        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);
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
        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);
        _inner.DisposeAsync().Returns(ValueTask.CompletedTask);

        // Act
        await orchestrator.DisposeAsync().ConfigureAwait(false);

        // Assert
        await _inner.Received(1).DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies decorator implements IWorkOrchestrator.
    /// </summary>
    [Test]
    public async Task AutoscalingOrchestrator_ImplementsIWorkOrchestrator()
    {
        // Arrange
        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);

        // Assert
        await Assert.That(orchestrator).IsAssignableTo<IWorkOrchestrator<string>>();
    }

    /// <summary>
    /// Verifies RequestScaleDownAsync stops no workers when no idle workers available.
    /// </summary>
    [Test]
    public async Task RequestScaleDownAsync_NoIdleWorkers_StopsNone()
    {
        // Arrange
        _registry.IdleWorkerCount.Returns(0);

        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);

        // Act
        await orchestrator.RequestScaleDownAsync(5).ConfigureAwait(false);

        // Assert - Should request stop for 0 workers
        _registry.Received(1).RequestMultipleWorkerStop(0);
    }
}
