// =============================================================================
// <copyright file="AutoscalingOrchestratorDisabledTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Threading.Channels;
using Levelup.Channels.Autoscaling;
using Levelup.Channels.Core;
using Levelup.Channels.Decorators;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TUnit.Core;

namespace Levelup.Channels.Tests.Decorators;

/// <summary>
/// Tests for <see cref="AutoscalingOrchestrator{TWork}"/> when disabled.
/// </summary>
[Property("Category", "Unit")]
public class AutoscalingOrchestratorDisabledTests
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
        _options = new AutoscalingOptions { Enabled = false };
        _logger = Substitute.For<ILogger<AutoscalingOrchestrator<string>>>();

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
    /// Verifies EnqueueAsync does not record metrics when disabled.
    /// </summary>
    [Test]
    public async Task WhenDisabled_EnqueueAsync_NoMetrics()
    {
        // Arrange
        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);
        _inner.EnqueueAsync("work", Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);

        // Act
        await orchestrator.EnqueueAsync("work").ConfigureAwait(false);

        // Assert
        _metrics.DidNotReceive().RecordEnqueue();
        await _inner.Received(1).EnqueueAsync("work", Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies TryEnqueue does not record metrics when disabled.
    /// </summary>
    [Test]
    public async Task WhenDisabled_TryEnqueue_NoMetrics()
    {
        // Arrange
        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);
        _inner.TryEnqueue("work").Returns(true);

        // Act
        var result = orchestrator.TryEnqueue("work");

        // Assert
        await Assert.That(result).IsTrue();
        _metrics.DidNotReceive().RecordEnqueue();
    }

    /// <summary>
    /// Verifies all calls pass through to inner when disabled.
    /// </summary>
    [Test]
    public async Task WhenDisabled_PureDelegation()
    {
        // Arrange
        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);
        _inner.EnqueueAsync("work", Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        _inner.StopAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _inner.DisposeAsync().Returns(ValueTask.CompletedTask);

        // Act
        await orchestrator.EnqueueAsync("work").ConfigureAwait(false);
        _ = orchestrator.PendingCount;
        _ = orchestrator.Capacity;
        _ = orchestrator.Writer;
        await orchestrator.StopAsync().ConfigureAwait(false);
        await orchestrator.DisposeAsync().ConfigureAwait(false);

        // Assert - verify delegation without metrics
        _metrics.DidNotReceive().RecordEnqueue();
        await _inner.Received(1).EnqueueAsync("work", Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await _inner.Received(1).StopAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await _inner.Received(1).DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies warning is logged when disabled.
    /// </summary>
    [Test]
    public async Task WhenDisabled_LogsWarning()
    {
        // Arrange & Act
        _ = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);

        // Assert - verify warning was logged
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("disabled", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// Verifies ActiveWorkers only returns inner count when disabled.
    /// </summary>
    [Test]
    public async Task WhenDisabled_ActiveWorkers_OnlyInnerCount()
    {
        // Arrange
        _inner.ActiveWorkers.Returns(5);
        _registry.ActiveWorkerCount.Returns(3);

        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);

        // Act
        var result = orchestrator.ActiveWorkers;

        // Assert - Should only use inner count when disabled
        await Assert.That(result).IsEqualTo(5);
    }

    /// <summary>
    /// Verifies RequestScaleUpAsync is a no-op when disabled.
    /// </summary>
    [Test]
    public async Task WhenDisabled_RequestScaleUpAsync_NoOp()
    {
        // Arrange
        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);

        // Act
        await orchestrator.RequestScaleUpAsync(5).ConfigureAwait(false);

        // Assert - no workers should be created
        await _registry.DidNotReceive().CreateWorkerAsync(
            Arg.Any<string>(),
            Arg.Any<Func<string, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies RequestScaleDownAsync is a no-op when disabled.
    /// </summary>
    [Test]
    public async Task WhenDisabled_RequestScaleDownAsync_NoOp()
    {
        // Arrange
        _registry.IdleWorkerCount.Returns(5);

        var orchestrator = new AutoscalingOrchestrator<string>(
            _inner, _registry, _metrics, _options, _logger);

        // Act
        await orchestrator.RequestScaleDownAsync(3).ConfigureAwait(false);

        // Assert - no workers should be stopped
        _registry.DidNotReceive().RequestMultipleWorkerStop(Arg.Any<int>());
    }
}
