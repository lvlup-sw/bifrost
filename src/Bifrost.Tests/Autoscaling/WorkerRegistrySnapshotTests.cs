// =============================================================================
// <copyright file="WorkerRegistrySnapshotTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;

using TUnit.Core;

namespace Bifrost.Tests.Autoscaling;

/// <summary>
/// Tests for <see cref="WorkerRegistrySnapshot"/> and <see cref="WorkerRegistry.GetSnapshot"/>.
/// </summary>
[Property("Category", "Unit")]
public class WorkerRegistrySnapshotTests
{
    /// <summary>
    /// Verifies that GetSnapshot returns correct values for an empty registry.
    /// </summary>
    [Test]
    public async Task GetSnapshot_EmptyRegistry_ReturnsZeroCounts()
    {
        var registry = new WorkerRegistry();

        var snapshot = registry.GetSnapshot();

        await Assert.That(snapshot.TotalWorkers).IsEqualTo(0);
        await Assert.That(snapshot.ActiveWorkers).IsEqualTo(0);
        await Assert.That(snapshot.IdleWorkers).IsEqualTo(0);
        await Assert.That(snapshot.BusyWorkers).IsEqualTo(0);
        await Assert.That(snapshot.StoppingWorkers).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that GetSnapshot returns correct timestamp.
    /// </summary>
    [Test]
    public async Task GetSnapshot_ReturnsRecentTimestamp()
    {
        var registry = new WorkerRegistry();
        var beforeSnapshot = DateTimeOffset.UtcNow;

        var snapshot = registry.GetSnapshot();

        await Assert.That(snapshot.Timestamp).IsGreaterThanOrEqualTo(beforeSnapshot);
        await Assert.That(snapshot.Timestamp).IsLessThanOrEqualTo(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Verifies that GetSnapshot counts total workers correctly.
    /// </summary>
    [Test]
    public async Task GetSnapshot_WithWorkers_CountsTotalCorrectly()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        for (var i = 0; i < 5; i++)
        {
            await registry.CreateWorkerAsync(
                $"worker-{i}",
                async (id, ct) => await Task.Delay(10000, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);
        }

        var snapshot = registry.GetSnapshot();

        await Assert.That(snapshot.TotalWorkers).IsEqualTo(5);
        await Assert.That(snapshot.ActiveWorkers).IsEqualTo(5);

        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that GetSnapshot correctly counts idle and busy workers.
    /// </summary>
    /// <remarks>
    /// The test creates 3 idle workers and 2 busy workers.
    /// </remarks>
    [Test]
    public async Task GetSnapshot_WithMixedStates_CountsCorrectly()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        for (var i = 0; i < 3; i++)
        {
            var worker = await registry.CreateWorkerAsync(
                $"idle-worker-{i}",
                async (id, ct) => await Task.Delay(10000, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);
            worker.MarkIdle();
        }

        for (var i = 0; i < 2; i++)
        {
            var worker = await registry.CreateWorkerAsync(
                $"busy-worker-{i}",
                async (id, ct) => await Task.Delay(10000, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);
            worker.MarkBusy();
        }

        var snapshot = registry.GetSnapshot();

        await Assert.That(snapshot.TotalWorkers).IsEqualTo(5);
        await Assert.That(snapshot.ActiveWorkers).IsEqualTo(5);
        await Assert.That(snapshot.IdleWorkers).IsEqualTo(3);
        await Assert.That(snapshot.BusyWorkers).IsEqualTo(2);

        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that GetSnapshot correctly counts workers requested to stop.
    /// </summary>
    /// <remarks>
    /// The test creates 3 workers and requests a stop for 2 of them.
    /// </remarks>
    [Test]
    public async Task GetSnapshot_WithStoppingWorkers_CountsCorrectly()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        for (var i = 0; i < 3; i++)
        {
            await registry.CreateWorkerAsync(
                $"worker-{i}",
                async (id, ct) => await Task.Delay(10000, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);
        }

        registry.RequestWorkerStop("worker-0");
        registry.RequestWorkerStop("worker-1");

        var snapshot = registry.GetSnapshot();

        await Assert.That(snapshot.TotalWorkers).IsEqualTo(3);
        await Assert.That(snapshot.ActiveWorkers).IsEqualTo(3);
        await Assert.That(snapshot.StoppingWorkers).IsEqualTo(2);

        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that WorkerRegistrySnapshot is immutable (record type).
    /// </summary>
    [Test]
    public async Task WorkerRegistrySnapshot_IsImmutableRecord()
    {
        var snapshot = new WorkerRegistrySnapshot(
            TotalWorkers: 10,
            ActiveWorkers: 8,
            IdleWorkers: 5,
            BusyWorkers: 3,
            StoppingWorkers: 2,
            Timestamp: DateTimeOffset.UtcNow);

        await Assert.That(snapshot.TotalWorkers).IsEqualTo(10);
        await Assert.That(snapshot.ActiveWorkers).IsEqualTo(8);
        await Assert.That(snapshot.IdleWorkers).IsEqualTo(5);
        await Assert.That(snapshot.BusyWorkers).IsEqualTo(3);
        await Assert.That(snapshot.StoppingWorkers).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies that WorkerRegistrySnapshot supports with-expressions (immutability).
    /// </summary>
    /// <remarks>
    /// The <c>with</c> expression makes a new snapshot with one changed value. The original stays
    /// unchanged, and the other values stay the same.
    /// </remarks>
    [Test]
    public async Task WorkerRegistrySnapshot_SupportsWithExpressions()
    {
        var original = new WorkerRegistrySnapshot(
            TotalWorkers: 10,
            ActiveWorkers: 8,
            IdleWorkers: 5,
            BusyWorkers: 3,
            StoppingWorkers: 2,
            Timestamp: DateTimeOffset.UtcNow);

        var modified = original with { IdleWorkers = 7 };

        await Assert.That(original.IdleWorkers).IsEqualTo(5);
        await Assert.That(modified.IdleWorkers).IsEqualTo(7);

        await Assert.That(modified.TotalWorkers).IsEqualTo(original.TotalWorkers);
    }

    /// <summary>
    /// Verifies that idle workers with stop-requested are NOT counted as busy.
    /// This is a regression test for the bug where busy = active - idle misclassified
    /// idle workers that had stop requested as busy workers.
    /// </summary>
    /// <remarks>
    /// The test creates 2 idle workers and 1 busy worker. Then it requests a stop on one idle
    /// worker, which was the bug scenario. The idle count includes only idle-worker-1 (no stop
    /// requested). The busy count includes only busy-worker. The stopping count includes only
    /// idle-worker-0. The invariant idle + busy + stopping = active must hold.
    /// </remarks>
    [Test]
    public async Task GetSnapshot_IdleWorkersWithStopRequested_NotCountedAsBusy()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        for (var i = 0; i < 2; i++)
        {
            var worker = await registry.CreateWorkerAsync(
                $"idle-worker-{i}",
                async (id, ct) => await Task.Delay(10000, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);
            worker.MarkIdle();
        }

        var busyWorker = await registry.CreateWorkerAsync(
            "busy-worker",
            async (id, ct) => await Task.Delay(10000, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);
        busyWorker.MarkBusy();

        registry.RequestWorkerStop("idle-worker-0");

        var snapshot = registry.GetSnapshot();

        await Assert.That(snapshot.TotalWorkers).IsEqualTo(3);
        await Assert.That(snapshot.ActiveWorkers).IsEqualTo(3);
        await Assert.That(snapshot.IdleWorkers).IsEqualTo(1);
        await Assert.That(snapshot.BusyWorkers).IsEqualTo(1);
        await Assert.That(snapshot.StoppingWorkers).IsEqualTo(1);

        await Assert.That(snapshot.IdleWorkers + snapshot.BusyWorkers + snapshot.StoppingWorkers)
            .IsEqualTo(snapshot.ActiveWorkers);

        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies the snapshot invariant: IdleWorkers + BusyWorkers + StoppingWorkers == ActiveWorkers.
    /// </summary>
    /// <remarks>
    /// The test creates a mix of idle, busy, and stopping workers. The stop requests go to an idle
    /// worker (worker-0) and a busy worker (worker-4), so both become stopping. The invariant idle
    /// + busy + stopping = active must hold. The individual counts are: idle 2 (worker-1,
    /// worker-2), busy 3 (worker-3, worker-5, worker-6), and stopping 2 (worker-0, worker-4).
    /// </remarks>
    [Test]
    public async Task GetSnapshot_SnapshotInvariant_IsCorrect()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        for (var i = 0; i < 7; i++)
        {
            var worker = await registry.CreateWorkerAsync(
                $"worker-{i}",
                async (id, ct) => await Task.Delay(10000, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);

            if (i < 3)
            {
                worker.MarkIdle();
            }
            else
            {
                worker.MarkBusy();
            }
        }

        registry.RequestWorkerStop("worker-0");
        registry.RequestWorkerStop("worker-4");

        var snapshot = registry.GetSnapshot();

        await Assert.That(snapshot.IdleWorkers + snapshot.BusyWorkers + snapshot.StoppingWorkers)
            .IsEqualTo(snapshot.ActiveWorkers);

        await Assert.That(snapshot.IdleWorkers).IsEqualTo(2);
        await Assert.That(snapshot.BusyWorkers).IsEqualTo(3);
        await Assert.That(snapshot.StoppingWorkers).IsEqualTo(2);

        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that GetSnapshot is consistent when called multiple times without changes.
    /// </summary>
    /// <remarks>
    /// The counts from the calls must be identical.
    /// </remarks>
    [Test]
    public async Task GetSnapshot_CalledMultipleTimes_ReturnsConsistentCounts()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        for (var i = 0; i < 3; i++)
        {
            var worker = await registry.CreateWorkerAsync(
                $"worker-{i}",
                async (id, ct) => await Task.Delay(10000, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);
            worker.MarkIdle();
        }

        var snapshot1 = registry.GetSnapshot();
        var snapshot2 = registry.GetSnapshot();

        await Assert.That(snapshot1.TotalWorkers).IsEqualTo(snapshot2.TotalWorkers);
        await Assert.That(snapshot1.ActiveWorkers).IsEqualTo(snapshot2.ActiveWorkers);
        await Assert.That(snapshot1.IdleWorkers).IsEqualTo(snapshot2.IdleWorkers);
        await Assert.That(snapshot1.BusyWorkers).IsEqualTo(snapshot2.BusyWorkers);
        await Assert.That(snapshot1.StoppingWorkers).IsEqualTo(snapshot2.StoppingWorkers);

        await cts.CancelAsync().ConfigureAwait(false);
    }
}