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
        // Arrange
        var registry = new WorkerRegistry();

        // Act
        var snapshot = registry.GetSnapshot();

        // Assert
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
        // Arrange
        var registry = new WorkerRegistry();
        var beforeSnapshot = DateTimeOffset.UtcNow;

        // Act
        var snapshot = registry.GetSnapshot();

        // Assert
        await Assert.That(snapshot.Timestamp).IsGreaterThanOrEqualTo(beforeSnapshot);
        await Assert.That(snapshot.Timestamp).IsLessThanOrEqualTo(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Verifies that GetSnapshot counts total workers correctly.
    /// </summary>
    [Test]
    public async Task GetSnapshot_WithWorkers_CountsTotalCorrectly()
    {
        // Arrange
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        for (var i = 0; i < 5; i++)
        {
            await registry.CreateWorkerAsync(
                $"worker-{i}",
                async (id, ct) => await Task.Delay(10000, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);
        }

        // Act
        var snapshot = registry.GetSnapshot();

        // Assert
        await Assert.That(snapshot.TotalWorkers).IsEqualTo(5);
        await Assert.That(snapshot.ActiveWorkers).IsEqualTo(5);

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that GetSnapshot correctly counts idle and busy workers.
    /// </summary>
    [Test]
    public async Task GetSnapshot_WithMixedStates_CountsCorrectly()
    {
        // Arrange
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        // Create 3 idle workers
        for (var i = 0; i < 3; i++)
        {
            var worker = await registry.CreateWorkerAsync(
                $"idle-worker-{i}",
                async (id, ct) => await Task.Delay(10000, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);
            worker.MarkIdle();
        }

        // Create 2 busy workers
        for (var i = 0; i < 2; i++)
        {
            var worker = await registry.CreateWorkerAsync(
                $"busy-worker-{i}",
                async (id, ct) => await Task.Delay(10000, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);
            worker.MarkBusy();
        }

        // Act
        var snapshot = registry.GetSnapshot();

        // Assert
        await Assert.That(snapshot.TotalWorkers).IsEqualTo(5);
        await Assert.That(snapshot.ActiveWorkers).IsEqualTo(5);
        await Assert.That(snapshot.IdleWorkers).IsEqualTo(3);
        await Assert.That(snapshot.BusyWorkers).IsEqualTo(2);

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that GetSnapshot correctly counts workers requested to stop.
    /// </summary>
    [Test]
    public async Task GetSnapshot_WithStoppingWorkers_CountsCorrectly()
    {
        // Arrange
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        // Create 3 workers
        for (var i = 0; i < 3; i++)
        {
            await registry.CreateWorkerAsync(
                $"worker-{i}",
                async (id, ct) => await Task.Delay(10000, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);
        }

        // Request stop for 2 workers
        registry.RequestWorkerStop("worker-0");
        registry.RequestWorkerStop("worker-1");

        // Act
        var snapshot = registry.GetSnapshot();

        // Assert
        await Assert.That(snapshot.TotalWorkers).IsEqualTo(3);
        await Assert.That(snapshot.ActiveWorkers).IsEqualTo(3);
        await Assert.That(snapshot.StoppingWorkers).IsEqualTo(2);

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that WorkerRegistrySnapshot is immutable (record type).
    /// </summary>
    [Test]
    public async Task WorkerRegistrySnapshot_IsImmutableRecord()
    {
        // Arrange
        var snapshot = new WorkerRegistrySnapshot(
            TotalWorkers: 10,
            ActiveWorkers: 8,
            IdleWorkers: 5,
            BusyWorkers: 3,
            StoppingWorkers: 2,
            Timestamp: DateTimeOffset.UtcNow);

        // Assert - Verify record properties are accessible
        await Assert.That(snapshot.TotalWorkers).IsEqualTo(10);
        await Assert.That(snapshot.ActiveWorkers).IsEqualTo(8);
        await Assert.That(snapshot.IdleWorkers).IsEqualTo(5);
        await Assert.That(snapshot.BusyWorkers).IsEqualTo(3);
        await Assert.That(snapshot.StoppingWorkers).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies that WorkerRegistrySnapshot supports with-expressions (immutability).
    /// </summary>
    [Test]
    public async Task WorkerRegistrySnapshot_SupportsWithExpressions()
    {
        // Arrange
        var original = new WorkerRegistrySnapshot(
            TotalWorkers: 10,
            ActiveWorkers: 8,
            IdleWorkers: 5,
            BusyWorkers: 3,
            StoppingWorkers: 2,
            Timestamp: DateTimeOffset.UtcNow);

        // Act - Create new snapshot with modified value
        var modified = original with { IdleWorkers = 7 };

        // Assert - Original is unchanged
        await Assert.That(original.IdleWorkers).IsEqualTo(5);
        await Assert.That(modified.IdleWorkers).IsEqualTo(7);

        // Other values remain the same
        await Assert.That(modified.TotalWorkers).IsEqualTo(original.TotalWorkers);
    }

    /// <summary>
    /// Verifies that idle workers with stop-requested are NOT counted as busy.
    /// This is a regression test for the bug where busy = active - idle misclassified
    /// idle workers that had stop requested as busy workers.
    /// </summary>
    [Test]
    public async Task GetSnapshot_IdleWorkersWithStopRequested_NotCountedAsBusy()
    {
        // Arrange
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        // Create 2 idle workers
        for (var i = 0; i < 2; i++)
        {
            var worker = await registry.CreateWorkerAsync(
                $"idle-worker-{i}",
                async (id, ct) => await Task.Delay(10000, ct).ConfigureAwait(false),
                cts.Token).ConfigureAwait(false);
            worker.MarkIdle();
        }

        // Create 1 busy worker
        var busyWorker = await registry.CreateWorkerAsync(
            "busy-worker",
            async (id, ct) => await Task.Delay(10000, ct).ConfigureAwait(false),
            cts.Token).ConfigureAwait(false);
        busyWorker.MarkBusy();

        // Request stop on one idle worker (this was the bug scenario)
        registry.RequestWorkerStop("idle-worker-0");

        // Act
        var snapshot = registry.GetSnapshot();

        // Assert
        await Assert.That(snapshot.TotalWorkers).IsEqualTo(3);
        await Assert.That(snapshot.ActiveWorkers).IsEqualTo(3);
        await Assert.That(snapshot.IdleWorkers).IsEqualTo(1); // Only idle-worker-1 (not stop-requested)
        await Assert.That(snapshot.BusyWorkers).IsEqualTo(1); // Only busy-worker (not stop-requested)
        await Assert.That(snapshot.StoppingWorkers).IsEqualTo(1); // Only idle-worker-0

        // Verify invariant: idle + busy + stopping = active
        await Assert.That(snapshot.IdleWorkers + snapshot.BusyWorkers + snapshot.StoppingWorkers)
            .IsEqualTo(snapshot.ActiveWorkers);

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies the snapshot invariant: IdleWorkers + BusyWorkers + StoppingWorkers == ActiveWorkers.
    /// </summary>
    [Test]
    public async Task GetSnapshot_SnapshotInvariant_IsCorrect()
    {
        // Arrange
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();

        // Create mix of idle, busy, and stopping workers
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

        // Request stop for some workers (mix of idle and busy)
        registry.RequestWorkerStop("worker-0"); // idle becoming stopping
        registry.RequestWorkerStop("worker-4"); // busy becoming stopping

        // Act
        var snapshot = registry.GetSnapshot();

        // Assert - Verify invariant: idle + busy + stopping = active
        await Assert.That(snapshot.IdleWorkers + snapshot.BusyWorkers + snapshot.StoppingWorkers)
            .IsEqualTo(snapshot.ActiveWorkers);

        // Verify individual counts
        await Assert.That(snapshot.IdleWorkers).IsEqualTo(2); // worker-1, worker-2
        await Assert.That(snapshot.BusyWorkers).IsEqualTo(3); // worker-3, worker-5, worker-6
        await Assert.That(snapshot.StoppingWorkers).IsEqualTo(2); // worker-0, worker-4

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that GetSnapshot is consistent when called multiple times without changes.
    /// </summary>
    [Test]
    public async Task GetSnapshot_CalledMultipleTimes_ReturnsConsistentCounts()
    {
        // Arrange
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

        // Act
        var snapshot1 = registry.GetSnapshot();
        var snapshot2 = registry.GetSnapshot();

        // Assert - Counts should be identical
        await Assert.That(snapshot1.TotalWorkers).IsEqualTo(snapshot2.TotalWorkers);
        await Assert.That(snapshot1.ActiveWorkers).IsEqualTo(snapshot2.ActiveWorkers);
        await Assert.That(snapshot1.IdleWorkers).IsEqualTo(snapshot2.IdleWorkers);
        await Assert.That(snapshot1.BusyWorkers).IsEqualTo(snapshot2.BusyWorkers);
        await Assert.That(snapshot1.StoppingWorkers).IsEqualTo(snapshot2.StoppingWorkers);

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
    }
}
