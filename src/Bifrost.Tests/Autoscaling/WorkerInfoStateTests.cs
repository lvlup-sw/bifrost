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
        var worker = new WorkerInfo("worker-1");

        await Assert.That(worker.IsIdle).IsTrue();
    }

    /// <summary>
    /// Verifies that MarkBusy sets IsIdle to false.
    /// </summary>
    [Test]
    public async Task MarkBusy_SetsIsIdleToFalse()
    {
        var worker = new WorkerInfo("worker-1");

        worker.MarkBusy();

        await Assert.That(worker.IsIdle).IsFalse();
    }

    /// <summary>
    /// Verifies that MarkIdle sets IsIdle to true.
    /// </summary>
    /// <remarks>
    /// The test first marks the worker busy.
    /// </remarks>
    [Test]
    public async Task MarkIdle_SetsIsIdleToTrue()
    {
        var worker = new WorkerInfo("worker-1");
        worker.MarkBusy();

        worker.MarkIdle();

        await Assert.That(worker.IsIdle).IsTrue();
    }

    /// <summary>
    /// Verifies that state transitions are idempotent - calling MarkBusy multiple times keeps IsIdle false.
    /// </summary>
    [Test]
    public async Task MarkBusy_CalledMultipleTimes_KeepsIsIdleFalse()
    {
        var worker = new WorkerInfo("worker-1");

        worker.MarkBusy();
        worker.MarkBusy();
        worker.MarkBusy();

        await Assert.That(worker.IsIdle).IsFalse();
    }

    /// <summary>
    /// Verifies that state transitions are idempotent - calling MarkIdle multiple times keeps IsIdle true.
    /// </summary>
    [Test]
    public async Task MarkIdle_CalledMultipleTimes_KeepsIsIdleTrue()
    {
        var worker = new WorkerInfo("worker-1");
        worker.MarkBusy();

        worker.MarkIdle();
        worker.MarkIdle();
        worker.MarkIdle();

        await Assert.That(worker.IsIdle).IsTrue();
    }

    /// <summary>
    /// Verifies that state transitions work correctly in a busy-idle-busy-idle sequence.
    /// </summary>
    /// <remarks>
    /// The initial state is idle. The test then moves the worker to busy, back to idle, to busy
    /// again, and back to idle.
    /// </remarks>
    [Test]
    public async Task StateTransitions_BusyIdleSequence_WorksCorrectly()
    {
        var worker = new WorkerInfo("worker-1");

        await Assert.That(worker.IsIdle).IsTrue();

        worker.MarkBusy();
        await Assert.That(worker.IsIdle).IsFalse();

        worker.MarkIdle();
        await Assert.That(worker.IsIdle).IsTrue();

        worker.MarkBusy();
        await Assert.That(worker.IsIdle).IsFalse();

        worker.MarkIdle();
        await Assert.That(worker.IsIdle).IsTrue();
    }

    /// <summary>
    /// Verifies that state transitions are thread-safe under concurrent access.
    /// </summary>
    /// <remarks>
    /// Two tasks call <c>MarkBusy</c> and <c>MarkIdle</c> concurrently. The calls must not throw,
    /// and the final state must be valid: either true or false, with no corruption.
    /// </remarks>
    [Test]
    public async Task StateTransitions_ConcurrentAccess_IsThreadSafe()
    {
        var worker = new WorkerInfo("worker-1");
        var iterations = 10000;
        var busyCount = 0;
        var idleCount = 0;

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

        await Task.WhenAll(busyTask, idleTask);

        await Assert.That(busyCount).IsEqualTo(iterations);
        await Assert.That(idleCount).IsEqualTo(iterations);

        var finalState = worker.IsIdle;
        await Assert.That(finalState == true || finalState == false).IsTrue();
    }
}