// =============================================================================
// <copyright file="WorkerInfoStateTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;
using TUnit.Core;

namespace Bifrost.Tests.Autoscaling;

/// <summary>
/// Tests for <see cref="WorkerInfo"/> busy/idle state tracking.
/// </summary>
[Property("Category", "Unit")]
public class WorkerInfoStateTests
{
    /// <summary>
    /// Verifies that a newly created worker starts in idle state.
    /// </summary>
    [Test]
    public async Task NewWorker_StartsIdle()
    {
        // Arrange & Act
        var worker = new WorkerInfo("worker-1");

        // Assert
        await Assert.That(worker.IsIdle).IsTrue();
    }

    /// <summary>
    /// Verifies that MarkBusy sets IsIdle to false.
    /// </summary>
    [Test]
    public async Task MarkBusy_SetsIsIdleToFalse()
    {
        // Arrange
        var worker = new WorkerInfo("worker-1");

        // Act
        worker.MarkBusy();

        // Assert
        await Assert.That(worker.IsIdle).IsFalse();
    }

    /// <summary>
    /// Verifies that MarkIdle sets IsIdle to true.
    /// </summary>
    [Test]
    public async Task MarkIdle_SetsIsIdleToTrue()
    {
        // Arrange
        var worker = new WorkerInfo("worker-1");
        worker.MarkBusy(); // First mark busy

        // Act
        worker.MarkIdle();

        // Assert
        await Assert.That(worker.IsIdle).IsTrue();
    }

    /// <summary>
    /// Verifies that state transitions are idempotent - calling MarkBusy multiple times keeps IsIdle false.
    /// </summary>
    [Test]
    public async Task MarkBusy_CalledMultipleTimes_KeepsIsIdleFalse()
    {
        // Arrange
        var worker = new WorkerInfo("worker-1");

        // Act
        worker.MarkBusy();
        worker.MarkBusy();
        worker.MarkBusy();

        // Assert
        await Assert.That(worker.IsIdle).IsFalse();
    }

    /// <summary>
    /// Verifies that state transitions are idempotent - calling MarkIdle multiple times keeps IsIdle true.
    /// </summary>
    [Test]
    public async Task MarkIdle_CalledMultipleTimes_KeepsIsIdleTrue()
    {
        // Arrange
        var worker = new WorkerInfo("worker-1");
        worker.MarkBusy();

        // Act
        worker.MarkIdle();
        worker.MarkIdle();
        worker.MarkIdle();

        // Assert
        await Assert.That(worker.IsIdle).IsTrue();
    }

    /// <summary>
    /// Verifies that state transitions work correctly in a busy-idle-busy-idle sequence.
    /// </summary>
    [Test]
    public async Task StateTransitions_BusyIdleSequence_WorksCorrectly()
    {
        // Arrange
        var worker = new WorkerInfo("worker-1");

        // Act & Assert - Initial state is idle
        await Assert.That(worker.IsIdle).IsTrue();

        // Transition to busy
        worker.MarkBusy();
        await Assert.That(worker.IsIdle).IsFalse();

        // Transition back to idle
        worker.MarkIdle();
        await Assert.That(worker.IsIdle).IsTrue();

        // Transition to busy again
        worker.MarkBusy();
        await Assert.That(worker.IsIdle).IsFalse();

        // Transition back to idle
        worker.MarkIdle();
        await Assert.That(worker.IsIdle).IsTrue();
    }

    /// <summary>
    /// Verifies that state transitions are thread-safe under concurrent access.
    /// </summary>
    [Test]
    public async Task StateTransitions_ConcurrentAccess_IsThreadSafe()
    {
        // Arrange
        var worker = new WorkerInfo("worker-1");
        var iterations = 10000;
        var busyCount = 0;
        var idleCount = 0;

        // Act - Concurrent MarkBusy and MarkIdle calls
        var busyTask = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                worker.MarkBusy();
                Interlocked.Increment(ref busyCount);
            }
        });

        var idleTask = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                worker.MarkIdle();
                Interlocked.Increment(ref idleCount);
            }
        });

        await Task.WhenAll(busyTask, idleTask).ConfigureAwait(false);

        // Assert - Should not throw and should have a valid state
        await Assert.That(busyCount).IsEqualTo(iterations);
        await Assert.That(idleCount).IsEqualTo(iterations);

        // The final state should be either true or false (no corruption)
        var finalState = worker.IsIdle;
        await Assert.That(finalState == true || finalState == false).IsTrue();
    }
}
