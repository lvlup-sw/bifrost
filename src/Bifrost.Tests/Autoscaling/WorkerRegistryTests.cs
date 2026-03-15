// =============================================================================
// <copyright file="WorkerRegistryTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;

using Microsoft.Extensions.Logging;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.Autoscaling;

/// <summary>
/// Tests for <see cref="WorkerRegistry"/> implementation.
/// </summary>
[Property("Category", "Unit")]
public class WorkerRegistryTests
{
    /// <summary>
    /// Verifies that initial active worker count is zero.
    /// </summary>
    [Test]
    public async Task ActiveWorkerCount_Initial_ReturnsZero()
    {
        // Arrange
        var registry = new WorkerRegistry();

        // Assert
        await Assert.That(registry.ActiveWorkerCount).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that initial idle worker count is zero.
    /// </summary>
    [Test]
    public async Task IdleWorkerCount_Initial_ReturnsZero()
    {
        // Arrange
        var registry = new WorkerRegistry();

        // Assert
        await Assert.That(registry.IdleWorkerCount).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies CreateWorkerAsync adds a worker.
    /// </summary>
    [Test]
    public async Task CreateWorkerAsync_AddsWorker()
    {
        // Arrange
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        // Act
        var workerInfo = await registry.CreateWorkerAsync(
            "worker-1",
            async (id, ct) => await Task.Delay(100, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);

        // Assert
        await Assert.That(registry.ActiveWorkerCount).IsEqualTo(1);
        await Assert.That(workerInfo.WorkerId).IsEqualTo("worker-1");

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies CreateWorkerAsync returns correct worker info.
    /// </summary>
    [Test]
    public async Task CreateWorkerAsync_ReturnsCorrectWorkerInfo()
    {
        // Arrange
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();
        var beforeCreate = DateTimeOffset.UtcNow;

        // Act
        var workerInfo = await registry.CreateWorkerAsync(
            "test-worker",
            async (id, ct) => await Task.Delay(100, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);

        // Assert
        await Assert.That(workerInfo.WorkerId).IsEqualTo("test-worker");
        await Assert.That(workerInfo.CreatedAt).IsGreaterThanOrEqualTo(beforeCreate);
        await Assert.That(workerInfo.StopRequested).IsFalse();

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies GetAllWorkers returns all registered workers.
    /// </summary>
    [Test]
    public async Task GetAllWorkers_ReturnsAllWorkers()
    {
        // Arrange
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        // Act
        await registry.CreateWorkerAsync(
            "worker-1",
            async (id, ct) => await Task.Delay(1000, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);
        await registry.CreateWorkerAsync(
            "worker-2",
            async (id, ct) => await Task.Delay(1000, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);

        var workers = registry.GetAllWorkers();

        // Assert
        await Assert.That(workers.Count).IsEqualTo(2);

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies RequestWorkerStop sets stop flag and returns true.
    /// </summary>
    [Test]
    public async Task RequestWorkerStop_SetsStopFlag_ReturnsTrue()
    {
        // Arrange
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        await registry.CreateWorkerAsync(
            "worker-1",
            async (id, ct) => await Task.Delay(1000, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);

        // Act
        var result = registry.RequestWorkerStop("worker-1");

        // Assert
        await Assert.That(result).IsTrue();
        var workers = registry.GetAllWorkers();
        var worker = workers.First(w => w.WorkerId == "worker-1");
        await Assert.That(worker.StopRequested).IsTrue();

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies RequestWorkerStop returns false for non-existent worker.
    /// </summary>
    [Test]
    public async Task RequestWorkerStop_NonExistent_ReturnsFalse()
    {
        // Arrange
        var registry = new WorkerRegistry();

        // Act
        var result = registry.RequestWorkerStop("non-existent");

        // Assert
        await Assert.That(result).IsFalse();
    }

    /// <summary>
    /// Verifies RequestMultipleWorkerStop stops idle workers first.
    /// </summary>
    [Test]
    public async Task RequestMultipleWorkerStop_StopsIdleWorkersFirst()
    {
        // Arrange
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        var worker1 = await registry.CreateWorkerAsync(
            "idle-worker",
            async (id, ct) => await Task.Delay(1000, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);
        worker1.IsIdle = true;

        var worker2 = await registry.CreateWorkerAsync(
            "busy-worker",
            async (id, ct) => await Task.Delay(1000, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);
        worker2.IsIdle = false;

        // Act
        var stoppedCount = registry.RequestMultipleWorkerStop(1);

        // Assert - Should stop the idle worker first
        await Assert.That(stoppedCount).IsEqualTo(1);
        var workers = registry.GetAllWorkers();
        var idleWorker = workers.First(w => w.WorkerId == "idle-worker");
        var busyWorker = workers.First(w => w.WorkerId == "busy-worker");
        await Assert.That(idleWorker.StopRequested).IsTrue();
        await Assert.That(busyWorker.StopRequested).IsFalse();

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies RequestMultipleWorkerStop respects requested count.
    /// </summary>
    [Test]
    public async Task RequestMultipleWorkerStop_RespectsCount()
    {
        // Arrange
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        for (var i = 0; i < 5; i++)
        {
            var worker = await registry.CreateWorkerAsync(
                $"worker-{i}",
                async (id, ct) => await Task.Delay(1000, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);
            worker.IsIdle = true;
        }

        // Act
        var stoppedCount = registry.RequestMultipleWorkerStop(2);

        // Assert
        await Assert.That(stoppedCount).IsEqualTo(2);
        var stoppedWorkers = registry.GetAllWorkers().Count(w => w.StopRequested);
        await Assert.That(stoppedWorkers).IsEqualTo(2);

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies RequestMultipleWorkerStop selects newest workers first (LIFO).
    /// </summary>
    [Test]
    public async Task RequestMultipleWorkerStop_SelectsNewestFirst()
    {
        // Arrange
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        // Create workers with slight delay to ensure different creation times
        for (var i = 0; i < 3; i++)
        {
            var worker = await registry.CreateWorkerAsync(
                $"worker-{i}",
                async (id, ct) => await Task.Delay(1000, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);
            worker.IsIdle = true;
            await Task.Delay(10).ConfigureAwait(false); // Ensure different creation times
        }

        // Act - Request to stop 1 worker
        var stoppedCount = registry.RequestMultipleWorkerStop(1);

        // Assert - Newest (worker-2) should be stopped first
        await Assert.That(stoppedCount).IsEqualTo(1);
        var workers = registry.GetAllWorkers();
        var worker2 = workers.First(w => w.WorkerId == "worker-2");
        await Assert.That(worker2.StopRequested).IsTrue();

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies RequestMultipleWorkerStop returns actual count when less available.
    /// </summary>
    [Test]
    public async Task RequestMultipleWorkerStop_ReturnsActualCount_WhenLessAvailable()
    {
        // Arrange
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        var worker = await registry.CreateWorkerAsync(
            "worker-1",
            async (id, ct) => await Task.Delay(1000, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);
        worker.IsIdle = true;

        // Act - Request more than available
        var stoppedCount = registry.RequestMultipleWorkerStop(5);

        // Assert
        await Assert.That(stoppedCount).IsEqualTo(1);

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies IdleWorkerCount tracks idle workers correctly.
    /// </summary>
    [Test]
    public async Task IdleWorkerCount_TracksIdleWorkers()
    {
        // Arrange
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        var worker1 = await registry.CreateWorkerAsync(
            "worker-1",
            async (id, ct) => await Task.Delay(1000, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);
        var worker2 = await registry.CreateWorkerAsync(
            "worker-2",
            async (id, ct) => await Task.Delay(1000, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);

        // Act
        worker1.IsIdle = true;
        worker2.IsIdle = false;

        // Assert
        await Assert.That(registry.IdleWorkerCount).IsEqualTo(1);

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies interface implementation.
    /// </summary>
    [Test]
    public async Task WorkerRegistry_ImplementsIWorkerRegistry()
    {
        // Arrange & Act
        var registry = new WorkerRegistry();

        // Assert
        await Assert.That(registry).IsAssignableTo<IWorkerRegistry>();
    }

    /// <summary>
    /// Verifies GetWorkerInfo returns worker when exists.
    /// </summary>
    [Test]
    public async Task GetWorkerInfo_WhenExists_ReturnsWorker()
    {
        // Arrange
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        await registry.CreateWorkerAsync(
            "worker-1",
            async (id, ct) => await Task.Delay(1000, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);

        // Act
        var worker = registry.GetWorkerInfo("worker-1");

        // Assert
        await Assert.That(worker).IsNotNull();
        await Assert.That(worker!.WorkerId).IsEqualTo("worker-1");

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies GetWorkerInfo returns null when worker does not exist.
    /// </summary>
    [Test]
    public async Task GetWorkerInfo_WhenNotExists_ReturnsNull()
    {
        // Arrange
        var registry = new WorkerRegistry();

        // Act
        var worker = registry.GetWorkerInfo("non-existent");

        // Assert
        await Assert.That(worker).IsNull();
    }

    /// <summary>
    /// Verifies MarkBusy sets IsIdle to false.
    /// </summary>
    [Test]
    public async Task MarkBusy_SetsIsIdleToFalse()
    {
        // Arrange
        var workerInfo = new WorkerInfo("test-worker");
        workerInfo.IsIdle = true;

        // Act
        workerInfo.MarkBusy();

        // Assert
        await Assert.That(workerInfo.IsIdle).IsFalse();
    }

    /// <summary>
    /// Verifies MarkIdle sets IsIdle to true.
    /// </summary>
    [Test]
    public async Task MarkIdle_SetsIsIdleToTrue()
    {
        // Arrange
        var workerInfo = new WorkerInfo("test-worker");
        workerInfo.IsIdle = false;

        // Act
        workerInfo.MarkIdle();

        // Assert
        await Assert.That(workerInfo.IsIdle).IsTrue();
    }

    /// <summary>
    /// Verifies that when a worker function faults, the error is logged (M12).
    /// </summary>
    [Test]
    public async Task CreateWorkerAsync_WorkerFaults_LogsError()
    {
        // Arrange
        var logger = Substitute.For<ILogger<WorkerRegistry>>();
        var registry = new WorkerRegistry(logger);
        var faulted = new TaskCompletionSource();

        // Act - Create a worker that throws
        await registry.CreateWorkerAsync(
            "faulting-worker",
            (id, ct) => throw new InvalidOperationException("Worker fault"),
            CancellationToken.None).ConfigureAwait(false);

        // Wait for the fault to propagate through ContinueWith
        await Task.Delay(200).ConfigureAwait(false);

        // Assert - Error should be logged
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}