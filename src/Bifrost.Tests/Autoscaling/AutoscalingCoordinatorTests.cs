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
    private IWorkerRegistry _mockRegistry = null!;

    /// <summary>
    /// Sets up test dependencies.
    /// </summary>
    [Before(Test)]
    public Task Setup()
    {
        _mockOrchestrator = Substitute.For<IWorkOrchestrator<string>>();
        _mockRegistry = Substitute.For<IWorkerRegistry>();
        return Task.CompletedTask;
    }

    #region Constructor Tests

    /// <summary>
    /// Verifies that constructor throws when orchestrator is null.
    /// </summary>
    [Test]
    public async Task Constructor_NullOrchestrator_ThrowsArgumentNullException()
    {
        await Assert.That(() => new AutoscalingCoordinator<string>(null!, _mockRegistry))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that constructor throws when registry is null.
    /// </summary>
    [Test]
    public async Task Constructor_NullRegistry_ThrowsArgumentNullException()
    {
        await Assert.That(() => new AutoscalingCoordinator<string>(_mockOrchestrator, null!))
            .Throws<ArgumentNullException>();
    }

    #endregion

    #region IAutoscalingMetricsPort Tests

    /// <summary>
    /// Verifies that MaxBacklog delegates to orchestrator Capacity.
    /// </summary>
    [Test]
    public async Task MaxBacklog_DelegatesToOrchestratorCapacity()
    {
        _mockOrchestrator.Capacity.Returns(100);
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator, _mockRegistry);

        var result = coordinator.MaxBacklog;

        await Assert.That(result).IsEqualTo(100);
    }

    /// <summary>
    /// Verifies that PendingWorkCount delegates to orchestrator PendingCount.
    /// </summary>
    [Test]
    public async Task PendingWorkCount_DelegatesToOrchestratorPendingCount()
    {
        _mockOrchestrator.PendingCount.Returns(42);
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator, _mockRegistry);

        var result = coordinator.PendingWorkCount;

        await Assert.That(result).IsEqualTo(42);
    }

    /// <summary>
    /// Verifies that ActiveWorkerCount delegates to orchestrator ActiveWorkers.
    /// </summary>
    [Test]
    public async Task ActiveWorkerCount_DelegatesToOrchestratorActiveWorkers()
    {
        _mockOrchestrator.ActiveWorkers.Returns(5);
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator, _mockRegistry);

        var result = coordinator.ActiveWorkerCount;

        await Assert.That(result).IsEqualTo(5);
    }

    /// <summary>
    /// Verifies that GetUtilizationRatio returns correct ratio.
    /// </summary>
    [Test]
    public async Task GetUtilizationRatio_ReturnsCorrectRatio()
    {
        _mockOrchestrator.PendingCount.Returns(50);
        _mockOrchestrator.Capacity.Returns(100);
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator, _mockRegistry);

        var result = coordinator.GetUtilizationRatio();

        await Assert.That(result).IsEqualTo(0.5);
    }

    /// <summary>
    /// Verifies that GetUtilizationRatio returns 0 when capacity is 0 (division by zero protection).
    /// </summary>
    [Test]
    public async Task GetUtilizationRatio_ZeroCapacity_ReturnsZero()
    {
        _mockOrchestrator.PendingCount.Returns(50);
        _mockOrchestrator.Capacity.Returns(0);
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator, _mockRegistry);

        var result = coordinator.GetUtilizationRatio();

        await Assert.That(result).IsEqualTo(0.0);
    }

    /// <summary>
    /// Verifies that GetUtilizationRatio returns 1.0 when fully utilized.
    /// </summary>
    [Test]
    public async Task GetUtilizationRatio_FullyUtilized_ReturnsOne()
    {
        _mockOrchestrator.PendingCount.Returns(100);
        _mockOrchestrator.Capacity.Returns(100);
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator, _mockRegistry);

        var result = coordinator.GetUtilizationRatio();

        await Assert.That(result).IsEqualTo(1.0);
    }

    /// <summary>
    /// Verifies that GetUtilizationRatio returns value greater than 1 when over capacity.
    /// </summary>
    [Test]
    public async Task GetUtilizationRatio_OverCapacity_ReturnsGreaterThanOne()
    {
        _mockOrchestrator.PendingCount.Returns(150);
        _mockOrchestrator.Capacity.Returns(100);
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator, _mockRegistry);

        var result = coordinator.GetUtilizationRatio();

        await Assert.That(result).IsEqualTo(1.5);
    }

    /// <summary>
    /// Verifies that QueuedCount returns 0 (placeholder until event stream integration).
    /// </summary>
    [Test]
    public async Task QueuedCount_ReturnsExpectedValue()
    {
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator, _mockRegistry);

        var result = coordinator.QueuedCount;

        await Assert.That(result).IsEqualTo(0L);
    }

    #endregion

    #region Interface Tests

    /// <summary>
    /// Verifies that IAutoscalingCoordinator implements all three port interfaces.
    /// </summary>
    [Test]
    public async Task Interface_ImplementsAllPorts()
    {
        var type = typeof(IAutoscalingCoordinator);

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
        var type = typeof(AutoscalingCoordinator<string>);

        await Assert.That(typeof(IAutoscalingCoordinator).IsAssignableFrom(type)).IsTrue();
    }

    #endregion

    #region IAutoscalingControlPort Tests (7a-7b)

    /// <summary>
    /// Verifies that CreateWorkerFunction delegates to the inner orchestrator.
    /// </summary>
    [Test]
    public async Task CreateWorkerFunction_DelegatesToInnerOrchestrator()
    {
        Func<string, CancellationToken, Task> expectedFunc = (_, _) => Task.CompletedTask;
        _mockOrchestrator.CreateWorkerFunction().Returns(expectedFunc);
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator, _mockRegistry);

        var result = coordinator.CreateWorkerFunction();

        await Assert.That(result).IsEqualTo(expectedFunc);
        _mockOrchestrator.Received(1).CreateWorkerFunction();
    }

    /// <summary>
    /// Verifies that CreateWorkerFunction with callback delegates to the inner orchestrator.
    /// </summary>
    [Test]
    public async Task CreateWorkerFunction_WithCallback_DelegatesToInnerOrchestrator()
    {
        Action<bool> callback = _ => { };
        Func<string, CancellationToken, Task> expectedFunc = (_, _) => Task.CompletedTask;
        _mockOrchestrator.CreateWorkerFunction(callback).Returns(expectedFunc);
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator, _mockRegistry);

        var result = coordinator.CreateWorkerFunction(callback);

        await Assert.That(result).IsEqualTo(expectedFunc);
        _mockOrchestrator.Received(1).CreateWorkerFunction(callback);
    }

    #endregion

    #region IAutoscalingControlPort Tests (7c-7d)

    /// <summary>
    /// Verifies that RequestScaleUpAsync creates workers via registry.
    /// </summary>
    [Test]
    public async Task RequestScaleUpAsync_CreatesWorkersViaRegistry()
    {
        Func<string, CancellationToken, Task> workerFunc = (_, _) => Task.CompletedTask;
        _mockOrchestrator.CreateWorkerFunction().Returns(workerFunc);
        _mockRegistry.CreateWorkerAsync(
                Arg.Any<string>(),
                Arg.Any<Func<string, CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorkerInfo("test")));
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator, _mockRegistry);

        await coordinator.RequestScaleUpAsync(3, CancellationToken.None);

        await _mockRegistry.Received(3).CreateWorkerAsync(
            Arg.Any<string>(),
            Arg.Any<Func<string, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that RequestScaleDownAsync stops workers via registry.
    /// </summary>
    [Test]
    public async Task RequestScaleDownAsync_StopsWorkersViaRegistry()
    {
        _mockRegistry.RequestMultipleWorkerStop(3).Returns(3);
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator, _mockRegistry);

        await coordinator.RequestScaleDownAsync(3, CancellationToken.None);

        _mockRegistry.Received(1).RequestMultipleWorkerStop(3);
    }

    /// <summary>
    /// Verifies that GetShutdownToken delegates to orchestrator.
    /// </summary>
    [Test]
    public async Task GetShutdownToken_DelegatesToOrchestrator()
    {
        using var cts = new CancellationTokenSource();
        _mockOrchestrator.GetShutdownToken().Returns(cts.Token);
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator, _mockRegistry);

        var result = coordinator.GetShutdownToken();

        await Assert.That(result).IsEqualTo(cts.Token);
        _mockOrchestrator.Received(1).GetShutdownToken();
    }

    #endregion

    #region IAutoscalingEventsPort Tests (7e-7f)

    /// <summary>
    /// Verifies that PublishEvent with event stream callback delegates to it.
    /// </summary>
    [Test]
    public async Task PublishEvent_WithEventStream_DelegatesToEventStream()
    {
        IOrchestratorEvent? capturedEvent = null;
        Action<IOrchestratorEvent> publishCallback = evt => capturedEvent = evt;
        var coordinator = new AutoscalingCoordinator<string>(
            _mockOrchestrator, _mockRegistry, publishCallback);
        var mockEvent = Substitute.For<IOrchestratorEvent>();

        coordinator.PublishEvent(mockEvent);

        await Assert.That(capturedEvent).IsEqualTo(mockEvent);
    }

    /// <summary>
    /// Verifies that PublishEvent without event stream is a no-op.
    /// </summary>
    [Test]
    public async Task PublishEvent_WithoutEventStream_NoOp()
    {
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator, _mockRegistry);
        var mockEvent = Substitute.For<IOrchestratorEvent>();

        await Assert.That(() => coordinator.PublishEvent(mockEvent)).ThrowsNothing();
    }

    /// <summary>
    /// Verifies that QueuedEventCount returns 0 when no event stream.
    /// </summary>
    [Test]
    public async Task QueuedEventCount_ReturnsZeroWhenNoEventStream()
    {
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator, _mockRegistry);

        var result = coordinator.QueuedEventCount;

        await Assert.That(result).IsEqualTo(0L);
    }

    /// <summary>
    /// Verifies that ActiveSubscriberCount returns 0 when no event stream.
    /// </summary>
    [Test]
    public async Task ActiveSubscriberCount_ReturnsZeroWhenNoEventStream()
    {
        var coordinator = new AutoscalingCoordinator<string>(_mockOrchestrator, _mockRegistry);

        var result = coordinator.ActiveSubscriberCount;

        await Assert.That(result).IsEqualTo(0L);
    }

    #endregion
}
