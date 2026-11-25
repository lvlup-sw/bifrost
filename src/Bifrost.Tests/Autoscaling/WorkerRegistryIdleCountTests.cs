// =============================================================================
// <copyright file="WorkerRegistryIdleCountTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;

using TUnit.Core;

namespace Bifrost.Tests.Autoscaling;

/// <summary>
/// Tests for <see cref="WorkerRegistry.IdleWorkerCount"/> functionality.
/// </summary>
[Property("Category", "Unit")]
public class WorkerRegistryIdleCountTests
{
    /// <summary>
    /// Verifies that IdleWorkerCount returns zero when no workers exist.
    /// </summary>
    [Test]
    public async Task IdleWorkerCount_NoWorkers_ReturnsZero()
    {
        var registry = new WorkerRegistry();

        var idleCount = registry.IdleWorkerCount;

        await Assert.That(idleCount).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that IdleWorkerCount correctly counts idle workers.
    /// </summary>
    [Test]
    public async Task IdleWorkerCount_WithIdleWorkers_ReturnsCorrectCount()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        var worker1 = await registry.CreateWorkerAsync(
            "worker-1",
            async (id, ct) => await Task.Delay(10000, ct),
            cts.Token);
        worker1.MarkIdle();

        var worker2 = await registry.CreateWorkerAsync(
            "worker-2",
            async (id, ct) => await Task.Delay(10000, ct),
            cts.Token);
        worker2.MarkIdle();

        var idleCount = registry.IdleWorkerCount;

        await Assert.That(idleCount).IsEqualTo(2);

        await cts.CancelAsync();
    }

    /// <summary>
    /// Verifies that IdleWorkerCount excludes busy workers.
    /// </summary>
    [Test]
    public async Task IdleWorkerCount_WithBusyWorkers_ExcludesBusyWorkers()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        var worker1 = await registry.CreateWorkerAsync(
            "worker-1",
            async (id, ct) => await Task.Delay(10000, ct),
            cts.Token);
        worker1.MarkIdle();

        var worker2 = await registry.CreateWorkerAsync(
            "worker-2",
            async (id, ct) => await Task.Delay(10000, ct),
            cts.Token);
        worker2.MarkBusy();

        var idleCount = registry.IdleWorkerCount;

        await Assert.That(idleCount).IsEqualTo(1);

        await cts.CancelAsync();
    }

    /// <summary>
    /// Verifies that IdleWorkerCount excludes workers that have been requested to stop.
    /// </summary>
    [Test]
    public async Task IdleWorkerCount_WithStoppingWorkers_ExcludesStoppingWorkers()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        var worker1 = await registry.CreateWorkerAsync(
            "worker-1",
            async (id, ct) => await Task.Delay(10000, ct),
            cts.Token);
        worker1.MarkIdle();

        var worker2 = await registry.CreateWorkerAsync(
            "worker-2",
            async (id, ct) => await Task.Delay(10000, ct),
            cts.Token);
        worker2.MarkIdle();
        registry.RequestWorkerStop("worker-2");

        var idleCount = registry.IdleWorkerCount;

        await Assert.That(idleCount).IsEqualTo(1);

        await cts.CancelAsync();
    }

    /// <summary>
    /// Verifies that IdleWorkerCount reflects state changes correctly.
    /// </summary>
    /// <remarks>
    /// The workers start idle. The test then marks a worker busy, and then idle again.
    /// </remarks>
    [Test]
    public async Task IdleWorkerCount_StateChanges_ReflectsCorrectly()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        var worker = await registry.CreateWorkerAsync(
            "worker-1",
            async (id, ct) => await Task.Delay(10000, ct),
            cts.Token);

        await Assert.That(registry.IdleWorkerCount).IsEqualTo(1);

        worker.MarkBusy();
        await Assert.That(registry.IdleWorkerCount).IsEqualTo(0);

        worker.MarkIdle();
        await Assert.That(registry.IdleWorkerCount).IsEqualTo(1);

        await cts.CancelAsync();
    }

    /// <summary>
    /// Verifies that IdleWorkerCount is thread-safe under concurrent access.
    /// </summary>
    /// <remarks>
    /// State changes and count reads run concurrently. The count must always be valid (0-10), and
    /// the run must complete without errors.
    /// </remarks>
    [Test]
    public async Task IdleWorkerCount_ConcurrentAccess_IsThreadSafe()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();
        var workers = new List<WorkerInfo>();

        for (var i = 0; i < 10; i++)
        {
            var worker = await registry.CreateWorkerAsync(
                $"worker-{i}",
                async (id, ct) => await Task.Delay(60000, ct),
                cts.Token);
            workers.Add(worker);
        }

        var readTask = Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                var count = registry.IdleWorkerCount;

                if (count < 0 || count > 10)
                {
                    throw new InvalidOperationException($"Invalid idle count: {count}");
                }
            }
        });

        var writeTask = Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                foreach (var worker in workers)
                {
                    worker.MarkBusy();
                }

                foreach (var worker in workers)
                {
                    worker.MarkIdle();
                }
            }
        });

        await Task.WhenAll(readTask, writeTask);

        var finalCount = registry.IdleWorkerCount;
        await Assert.That(finalCount).IsGreaterThanOrEqualTo(0);
        await Assert.That(finalCount).IsLessThanOrEqualTo(10);

        await cts.CancelAsync();
    }
}