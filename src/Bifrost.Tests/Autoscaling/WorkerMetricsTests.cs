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
    /// Verifies that initial pending count is zero.
    /// </summary>
    [Test]
    public async Task PendingWorkCount_Initial_ReturnsZero()
    {
        // Arrange
        var metrics = new WorkerMetrics();

        // Assert
        await Assert.That(metrics.PendingWorkCount).IsEqualTo(0);
    }

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
    /// Verifies RecordEnqueue increments pending count.
    /// </summary>
    [Test]
    public async Task RecordEnqueue_IncrementsPendingCount()
    {
        // Arrange
        var metrics = new WorkerMetrics();

        // Act
        metrics.RecordEnqueue();
        metrics.RecordEnqueue();
        metrics.RecordEnqueue();

        // Assert
        await Assert.That(metrics.PendingWorkCount).IsEqualTo(3);
    }

    /// <summary>
    /// Verifies RecordDequeue decrements pending count.
    /// </summary>
    [Test]
    public async Task RecordDequeue_DecrementsPendingCount()
    {
        // Arrange
        var metrics = new WorkerMetrics();
        metrics.RecordEnqueue();
        metrics.RecordEnqueue();

        // Act
        metrics.RecordDequeue();

        // Assert
        await Assert.That(metrics.PendingWorkCount).IsEqualTo(1);
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
    /// Verifies that utilization ratio is calculated correctly.
    /// </summary>
    [Test]
    public async Task CalculateUtilizationRatio_ReturnsCorrectValue()
    {
        // Arrange
        var metrics = new WorkerMetrics();
        metrics.RecordEnqueue();
        metrics.RecordEnqueue();
        metrics.RecordEnqueue();
        metrics.RecordEnqueue();

        // Act
        var ratio = metrics.CalculateUtilizationRatio(maxBacklog: 10);

        // Assert - 4 pending / 10 max = 0.4
        await Assert.That(ratio).IsEqualTo(0.4);
    }

    /// <summary>
    /// Verifies that utilization ratio handles empty queue.
    /// </summary>
    [Test]
    public async Task CalculateUtilizationRatio_EmptyQueue_ReturnsZero()
    {
        // Arrange
        var metrics = new WorkerMetrics();

        // Act
        var ratio = metrics.CalculateUtilizationRatio(maxBacklog: 100);

        // Assert
        await Assert.That(ratio).IsEqualTo(0.0);
    }

    /// <summary>
    /// Verifies that utilization ratio caps at 1.0.
    /// </summary>
    [Test]
    public async Task CalculateUtilizationRatio_OverCapacity_ReturnsOne()
    {
        // Arrange
        var metrics = new WorkerMetrics();
        for (var i = 0; i < 150; i++)
        {
            metrics.RecordEnqueue();
        }

        // Act
        var ratio = metrics.CalculateUtilizationRatio(maxBacklog: 100);

        // Assert - Should cap at 1.0
        await Assert.That(ratio).IsEqualTo(1.0);
    }

    /// <summary>
    /// Verifies that utilization ratio handles zero max backlog.
    /// </summary>
    [Test]
    public async Task CalculateUtilizationRatio_ZeroMaxBacklog_ReturnsOne()
    {
        // Arrange
        var metrics = new WorkerMetrics();
        metrics.RecordEnqueue();

        // Act
        var ratio = metrics.CalculateUtilizationRatio(maxBacklog: 0);

        // Assert - Division by zero protection should return 1.0
        await Assert.That(ratio).IsEqualTo(1.0);
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

        // Act - Run parallel enqueue/dequeue operations
        await Task.WhenAll(
            Enumerable.Range(0, taskCount).Select(_ =>
                Task.Run(() =>
                {
                    for (var i = 0; i < operationsPerTask; i++)
                    {
                        metrics.RecordEnqueue();
                        metrics.RecordExecutionStart();
                        metrics.RecordDequeue();
                        metrics.RecordExecutionEnd();
                    }
                }))).ConfigureAwait(false);

        // Assert - After balanced operations, counts should be zero
        await Assert.That(metrics.PendingWorkCount).IsEqualTo(0);
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