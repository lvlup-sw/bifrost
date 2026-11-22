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
        var registry = new WorkerRegistry();

        await Assert.That(registry.ActiveWorkerCount).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that initial idle worker count is zero.
    /// </summary>
    [Test]
    public async Task IdleWorkerCount_Initial_ReturnsZero()
    {
        var registry = new WorkerRegistry();

        await Assert.That(registry.IdleWorkerCount).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies CreateWorkerAsync adds a worker.
    /// </summary>
    [Test]
    public async Task CreateWorkerAsync_AddsWorker()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        var workerInfo = await registry.CreateWorkerAsync(
            "worker-1",
            async (id, ct) => await Task.Delay(100, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);

        await Assert.That(registry.ActiveWorkerCount).IsEqualTo(1);
        await Assert.That(workerInfo.WorkerId).IsEqualTo("worker-1");

        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies CreateWorkerAsync returns correct worker info.
    /// </summary>
    [Test]
    public async Task CreateWorkerAsync_ReturnsCorrectWorkerInfo()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();
        var beforeCreate = DateTimeOffset.UtcNow;

        var workerInfo = await registry.CreateWorkerAsync(
            "test-worker",
            async (id, ct) => await Task.Delay(100, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);

        await Assert.That(workerInfo.WorkerId).IsEqualTo("test-worker");
        await Assert.That(workerInfo.CreatedAt).IsGreaterThanOrEqualTo(beforeCreate);
        await Assert.That(workerInfo.StopRequested).IsFalse();

        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies GetAllWorkers returns all registered workers.
    /// </summary>
    [Test]
    public async Task GetAllWorkers_ReturnsAllWorkers()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        await registry.CreateWorkerAsync(
            "worker-1",
            async (id, ct) => await Task.Delay(1000, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);
        await registry.CreateWorkerAsync(
            "worker-2",
            async (id, ct) => await Task.Delay(1000, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);

        var workers = registry.GetAllWorkers();

        await Assert.That(workers.Count).IsEqualTo(2);

        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies RequestWorkerStop sets stop flag and returns true.
    /// </summary>
    [Test]
    public async Task RequestWorkerStop_SetsStopFlag_ReturnsTrue()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        await registry.CreateWorkerAsync(
            "worker-1",
            async (id, ct) => await Task.Delay(1000, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);

        var result = registry.RequestWorkerStop("worker-1");

        await Assert.That(result).IsTrue();
        var workers = registry.GetAllWorkers();
        var worker = workers.First(w => w.WorkerId == "worker-1");
        await Assert.That(worker.StopRequested).IsTrue();

        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies RequestWorkerStop returns false for non-existent worker.
    /// </summary>
    [Test]
    public async Task RequestWorkerStop_NonExistent_ReturnsFalse()
    {
        var registry = new WorkerRegistry();

        var result = registry.RequestWorkerStop("non-existent");

        await Assert.That(result).IsFalse();
    }

    /// <summary>
    /// Verifies RequestMultipleWorkerStop stops idle workers first.
    /// </summary>
    [Test]
    public async Task RequestMultipleWorkerStop_StopsIdleWorkersFirst()
    {
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

        var stoppedCount = registry.RequestMultipleWorkerStop(1);

        await Assert.That(stoppedCount).IsEqualTo(1);
        var workers = registry.GetAllWorkers();
        var idleWorker = workers.First(w => w.WorkerId == "idle-worker");
        var busyWorker = workers.First(w => w.WorkerId == "busy-worker");
        await Assert.That(idleWorker.StopRequested).IsTrue();
        await Assert.That(busyWorker.StopRequested).IsFalse();

        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies RequestMultipleWorkerStop respects requested count.
    /// </summary>
    [Test]
    public async Task RequestMultipleWorkerStop_RespectsCount()
    {
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

        var stoppedCount = registry.RequestMultipleWorkerStop(2);

        await Assert.That(stoppedCount).IsEqualTo(2);
        var stoppedWorkers = registry.GetAllWorkers().Count(w => w.StopRequested);
        await Assert.That(stoppedWorkers).IsEqualTo(2);

        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies RequestMultipleWorkerStop selects newest workers first (LIFO).
    /// </summary>
    /// <remarks>
    /// The 10 ms delay between the creations gives each worker a different creation time. A request
    /// to stop 1 worker must stop the newest worker (worker-2) first.
    /// </remarks>
    [Test]
    public async Task RequestMultipleWorkerStop_SelectsNewestFirst()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        for (var i = 0; i < 3; i++)
        {
            var worker = await registry.CreateWorkerAsync(
                $"worker-{i}",
                async (id, ct) => await Task.Delay(1000, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);
            worker.IsIdle = true;
            await Task.Delay(10).ConfigureAwait(false);
        }

        var stoppedCount = registry.RequestMultipleWorkerStop(1);

        await Assert.That(stoppedCount).IsEqualTo(1);
        var workers = registry.GetAllWorkers();
        var worker2 = workers.First(w => w.WorkerId == "worker-2");
        await Assert.That(worker2.StopRequested).IsTrue();

        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies RequestMultipleWorkerStop returns actual count when less available.
    /// </summary>
    /// <remarks>
    /// The test requests more stops than there are workers.
    /// </remarks>
    [Test]
    public async Task RequestMultipleWorkerStop_ReturnsActualCount_WhenLessAvailable()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        var worker = await registry.CreateWorkerAsync(
            "worker-1",
            async (id, ct) => await Task.Delay(1000, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);
        worker.IsIdle = true;

        var stoppedCount = registry.RequestMultipleWorkerStop(5);

        await Assert.That(stoppedCount).IsEqualTo(1);

        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies IdleWorkerCount tracks idle workers correctly.
    /// </summary>
    [Test]
    public async Task IdleWorkerCount_TracksIdleWorkers()
    {
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

        worker1.IsIdle = true;
        worker2.IsIdle = false;

        await Assert.That(registry.IdleWorkerCount).IsEqualTo(1);

        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies interface implementation.
    /// </summary>
    [Test]
    public async Task WorkerRegistry_ImplementsIWorkerRegistry()
    {
        var registry = new WorkerRegistry();

        await Assert.That(registry).IsAssignableTo<IWorkerRegistry>();
    }

    /// <summary>
    /// Verifies GetWorkerInfo returns worker when exists.
    /// </summary>
    [Test]
    public async Task GetWorkerInfo_WhenExists_ReturnsWorker()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        await registry.CreateWorkerAsync(
            "worker-1",
            async (id, ct) => await Task.Delay(1000, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);

        var worker = registry.GetWorkerInfo("worker-1");

        await Assert.That(worker).IsNotNull();
        await Assert.That(worker!.WorkerId).IsEqualTo("worker-1");

        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies GetWorkerInfo returns null when worker does not exist.
    /// </summary>
    [Test]
    public async Task GetWorkerInfo_WhenNotExists_ReturnsNull()
    {
        var registry = new WorkerRegistry();

        var worker = registry.GetWorkerInfo("non-existent");

        await Assert.That(worker).IsNull();
    }

    /// <summary>
    /// Verifies MarkBusy sets IsIdle to false.
    /// </summary>
    [Test]
    public async Task MarkBusy_SetsIsIdleToFalse()
    {
        var workerInfo = new WorkerInfo("test-worker");
        workerInfo.IsIdle = true;

        workerInfo.MarkBusy();

        await Assert.That(workerInfo.IsIdle).IsFalse();
    }

    /// <summary>
    /// Verifies MarkIdle sets IsIdle to true.
    /// </summary>
    [Test]
    public async Task MarkIdle_SetsIsIdleToTrue()
    {
        var workerInfo = new WorkerInfo("test-worker");
        workerInfo.IsIdle = false;

        workerInfo.MarkIdle();

        await Assert.That(workerInfo.IsIdle).IsTrue();
    }

    /// <summary>
    /// Verifies that when a worker function faults, the error is logged (M12).
    /// </summary>
    /// <remarks>
    /// The logger signals <c>logReceived</c> when it receives an error call. The test creates a
    /// worker that throws, then waits for the <c>ContinueWith</c> fault handler to log. This wait
    /// is deterministic, with no fixed delay, so the signal guarantees that the error was logged.
    /// </remarks>
    [Test]
    public async Task CreateWorkerAsync_WorkerFaults_LogsError()
    {
        var logger = Substitute.For<ILogger<WorkerRegistry>>();
        var logReceived = new TaskCompletionSource();

        logger.When(l => l.Log(
                LogLevel.Error,
                Arg.Any<EventId>(),
                Arg.Any<object>(),
                Arg.Any<Exception?>(),
                Arg.Any<Func<object, Exception?, string>>()))
            .Do(_ => logReceived.TrySetResult());

        var registry = new WorkerRegistry(logger);

        await registry.CreateWorkerAsync(
            "faulting-worker",
            (id, ct) => throw new InvalidOperationException("Worker fault"),
            CancellationToken.None).ConfigureAwait(false);

        await logReceived.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}