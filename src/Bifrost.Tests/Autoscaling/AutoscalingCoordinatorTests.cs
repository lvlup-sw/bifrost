// =============================================================================
// <copyright file="AutoscalingCoordinatorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;
using Bifrost.Autoscaling.Ports;
using Bifrost.Core;
using Bifrost.Core.Events;
using NSubstitute;
using TUnit.Core;

namespace Bifrost.Tests.Autoscaling;

/// <summary>
/// Tests for <see cref="AutoscalingCoordinator{TWork}"/> implementation.
/// </summary>
[Property("Category", "Unit")]
public class AutoscalingCoordinatorTests
{
    private IWorkOrchestrator<string> _mockOrchestrator = null!;

    /// <summary>
    /// Sets up test dependencies.
    /// </summary>
    [Before(Test)]
    public Task Setup()
    {
        _mockOrchestrator = Substitute.For<IWorkOrchestrator<string>>();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies that constructor throws when orchestrator is null.
    /// </summary>
    [Test]
    public async Task Constructor_NullOrchestrator_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.That(() => new AutoscalingCoordinator<string>(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that MaxBacklog delegates to orchestrator Capacity.
    /// </summary>
    [Test]
    public async Task MaxBacklog_DelegatesToOrchestratorCapacity()
    {
        // Arrange
        _mockOrchestrator.Capacity.Returns(100);
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator);

        // Act
        var result = coordinator.MaxBacklog;

        // Assert
        await Assert.That(result).IsEqualTo(100);
    }

    /// <summary>
    /// Verifies that PendingWorkCount delegates to orchestrator PendingCount.
    /// </summary>
    [Test]
    public async Task PendingWorkCount_DelegatesToOrchestratorPendingCount()
    {
        // Arrange
        _mockOrchestrator.PendingCount.Returns(42);
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator);

        // Act
        var result = coordinator.PendingWorkCount;

        // Assert
        await Assert.That(result).IsEqualTo(42);
    }

    /// <summary>
    /// Verifies that ActiveWorkerCount delegates to orchestrator ActiveWorkers.
    /// </summary>
    [Test]
    public async Task ActiveWorkerCount_DelegatesToOrchestratorActiveWorkers()
    {
        // Arrange
        _mockOrchestrator.ActiveWorkers.Returns(5);
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator);

        // Act
        var result = coordinator.ActiveWorkerCount;

        // Assert
        await Assert.That(result).IsEqualTo(5);
    }

    /// <summary>
    /// Verifies that GetUtilizationRatio returns correct ratio.
    /// </summary>
    [Test]
    public async Task GetUtilizationRatio_ReturnsCorrectRatio()
    {
        // Arrange
        _mockOrchestrator.PendingCount.Returns(50);
        _mockOrchestrator.Capacity.Returns(100);
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator);

        // Act
        var result = coordinator.GetUtilizationRatio();

        // Assert
        await Assert.That(result).IsEqualTo(0.5);
    }

    /// <summary>
    /// Verifies that GetUtilizationRatio returns 0 when capacity is 0 (division by zero protection).
    /// </summary>
    [Test]
    public async Task GetUtilizationRatio_ZeroCapacity_ReturnsZero()
    {
        // Arrange
        _mockOrchestrator.PendingCount.Returns(50);
        _mockOrchestrator.Capacity.Returns(0);
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator);

        // Act
        var result = coordinator.GetUtilizationRatio();

        // Assert
        await Assert.That(result).IsEqualTo(0.0);
    }

    /// <summary>
    /// Verifies that GetUtilizationRatio returns 1.0 when fully utilized.
    /// </summary>
    [Test]
    public async Task GetUtilizationRatio_FullyUtilized_ReturnsOne()
    {
        // Arrange
        _mockOrchestrator.PendingCount.Returns(100);
        _mockOrchestrator.Capacity.Returns(100);
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator);

        // Act
        var result = coordinator.GetUtilizationRatio();

        // Assert
        await Assert.That(result).IsEqualTo(1.0);
    }

    /// <summary>
    /// Verifies that GetUtilizationRatio returns value greater than 1 when over capacity.
    /// </summary>
    [Test]
    public async Task GetUtilizationRatio_OverCapacity_ReturnsGreaterThanOne()
    {
        // Arrange (edge case - channel may report more than capacity temporarily)
        _mockOrchestrator.PendingCount.Returns(150);
        _mockOrchestrator.Capacity.Returns(100);
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator);

        // Act
        var result = coordinator.GetUtilizationRatio();

        // Assert
        await Assert.That(result).IsEqualTo(1.5);
    }

    /// <summary>
    /// Verifies that QueuedCount returns 0 (placeholder until event stream integration).
    /// </summary>
    [Test]
    public async Task QueuedCount_ReturnsExpectedValue()
    {
        // Arrange
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator);

        // Act
        var result = coordinator.QueuedCount;

        // Assert - Placeholder returns 0 until event stream integration
        await Assert.That(result).IsEqualTo(0L);
    }

    /// <summary>
    /// Verifies that IAutoscalingCoordinator implements all three port interfaces.
    /// </summary>
    [Test]
    public async Task Interface_ImplementsAllPorts()
    {
        // Arrange
        var type = typeof(IAutoscalingCoordinator);

        // Assert
        await Assert.That(typeof(IAutoscalingMetricsPort).IsAssignableFrom(type)).IsTrue();
        await Assert.That(typeof(IAutoscalingControlPort).IsAssignableFrom(type)).IsTrue();
        await Assert.That(typeof(IAutoscalingEventsPort).IsAssignableFrom(type)).IsTrue();
    }

    /// <summary>
    /// Verifies that AutoscalingCoordinator implements IAutoscalingCoordinator.
    /// </summary>
    [Test]
    public async Task Class_ImplementsIAutoscalingCoordinator()
    {
        // Arrange
        var type = typeof(AutoscalingCoordinator<string>);

        // Assert
        await Assert.That(typeof(IAutoscalingCoordinator).IsAssignableFrom(type)).IsTrue();
    }

    /// <summary>
    /// Verifies that RequestScaleUpAsync throws NotImplementedException (Phase 9 stub).
    /// </summary>
    [Test]
    public async Task RequestScaleUpAsync_ThrowsNotImplementedException()
    {
        // Arrange
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator);

        // Act & Assert
        await Assert.That(() => coordinator.RequestScaleUpAsync(1, CancellationToken.None))
            .Throws<NotImplementedException>();
    }

    /// <summary>
    /// Verifies that RequestScaleDownAsync throws NotImplementedException (Phase 9 stub).
    /// </summary>
    [Test]
    public async Task RequestScaleDownAsync_ThrowsNotImplementedException()
    {
        // Arrange
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator);

        // Act & Assert
        await Assert.That(() => coordinator.RequestScaleDownAsync(1, CancellationToken.None))
            .Throws<NotImplementedException>();
    }

    /// <summary>
    /// Verifies that CreateWorkerFunction throws NotImplementedException (Phase 9 stub).
    /// </summary>
    [Test]
    public async Task CreateWorkerFunction_ThrowsNotImplementedException()
    {
        // Arrange
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator);

        // Act & Assert
        await Assert.That(() => coordinator.CreateWorkerFunction())
            .Throws<NotImplementedException>();
    }

    /// <summary>
    /// Verifies that CreateWorkerFunction with callback throws NotImplementedException (Phase 9 stub).
    /// </summary>
    [Test]
    public async Task CreateWorkerFunctionWithCallback_ThrowsNotImplementedException()
    {
        // Arrange
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator);

        // Act & Assert
        await Assert.That(() => coordinator.CreateWorkerFunction(_ => { }))
            .Throws<NotImplementedException>();
    }

    /// <summary>
    /// Verifies that GetShutdownToken throws NotImplementedException (Phase 9 stub).
    /// </summary>
    [Test]
    public async Task GetShutdownToken_ThrowsNotImplementedException()
    {
        // Arrange
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator);

        // Act & Assert
        await Assert.That(() => coordinator.GetShutdownToken())
            .Throws<NotImplementedException>();
    }

    /// <summary>
    /// Verifies that PublishEvent throws NotImplementedException (Phase 9 stub).
    /// </summary>
    [Test]
    public async Task PublishEvent_ThrowsNotImplementedException()
    {
        // Arrange
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator);
        var mockEvent = Substitute.For<IOrchestratorEvent>();

        // Act & Assert
        await Assert.That(() => coordinator.PublishEvent(mockEvent))
            .Throws<NotImplementedException>();
    }

    /// <summary>
    /// Verifies that QueuedEventCount throws NotImplementedException (Phase 9 stub).
    /// </summary>
    [Test]
    public async Task QueuedEventCount_ThrowsNotImplementedException()
    {
        // Arrange
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator);

        // Act & Assert
        await Assert.That(() => _ = coordinator.QueuedEventCount)
            .Throws<NotImplementedException>();
    }

    /// <summary>
    /// Verifies that ActiveSubscriberCount throws NotImplementedException (Phase 9 stub).
    /// </summary>
    [Test]
    public async Task ActiveSubscriberCount_ThrowsNotImplementedException()
    {
        // Arrange
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator);

        // Act & Assert
        await Assert.That(() => _ = coordinator.ActiveSubscriberCount)
            .Throws<NotImplementedException>();
    }
}
