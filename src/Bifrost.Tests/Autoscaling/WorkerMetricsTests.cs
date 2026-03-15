// =============================================================================
// <copyright file="WorkerMetricsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;

using TUnit.Core;

namespace Bifrost.Tests.Autoscaling;

/// <summary>
/// Tests for <see cref="WorkerMetrics"/> implementation.
/// </summary>
[Property("Category", "Unit")]
public class WorkerMetricsTests
{
    /// <summary>
    /// Verifies that initial in-flight count is zero.
    /// </summary>
    [Test]
    public async Task InFlightCount_Initial_ReturnsZero()
    {
        // Arrange
        var metrics = new WorkerMetrics();

        // Assert
        await Assert.That(metrics.InFlightCount).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies RecordEnqueue does not throw.
    /// </summary>
    [Test]
    public async Task RecordEnqueue_DoesNotThrow()
    {
        // Arrange
        var metrics = new WorkerMetrics();

        // Act — RecordEnqueue is a signal for the autoscaling decorator
        metrics.RecordEnqueue();
        metrics.RecordEnqueue();
        metrics.RecordEnqueue();

        // Assert — still implements the interface correctly
        await Assert.That(metrics).IsAssignableTo<IWorkerMetrics>();
    }

    /// <summary>
    /// Verifies RecordExecutionStart increments in-flight count.
    /// </summary>
    [Test]
    public async Task RecordExecutionStart_IncrementsInFlightCount()
    {
        // Arrange
        var metrics = new WorkerMetrics();

        // Act
        metrics.RecordExecutionStart();
        metrics.RecordExecutionStart();

        // Assert
        await Assert.That(metrics.InFlightCount).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies RecordExecutionEnd decrements in-flight count.
    /// </summary>
    [Test]
    public async Task RecordExecutionEnd_DecrementsInFlightCount()
    {
        // Arrange
        var metrics = new WorkerMetrics();
        metrics.RecordExecutionStart();
        metrics.RecordExecutionStart();

        // Act
        metrics.RecordExecutionEnd();

        // Assert
        await Assert.That(metrics.InFlightCount).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies that metrics are thread-safe under concurrent access.
    /// </summary>
    [Test]
    public async Task Metrics_ThreadSafe_HandlesParallelOperations()
    {
        // Arrange
        var metrics = new WorkerMetrics();
        const int operationsPerTask = 1000;
        const int taskCount = 10;

        // Act - Run parallel execution start/end operations
        await Task.WhenAll(
            Enumerable.Range(0, taskCount).Select(_ =>
                Task.Run(() =>
                {
                    for (var i = 0; i < operationsPerTask; i++)
                    {
                        metrics.RecordEnqueue();
                        metrics.RecordExecutionStart();
                        metrics.RecordExecutionEnd();
                    }
                }))).ConfigureAwait(false);

        // Assert - After balanced operations, in-flight count should be zero
        await Assert.That(metrics.InFlightCount).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies interface implementation.
    /// </summary>
    [Test]
    public async Task WorkerMetrics_ImplementsIWorkerMetrics()
    {
        // Arrange & Act
        var metrics = new WorkerMetrics();

        // Assert
        await Assert.That(metrics).IsAssignableTo<IWorkerMetrics>();
    }
}
