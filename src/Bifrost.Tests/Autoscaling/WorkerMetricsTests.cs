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
        var metrics = new WorkerMetrics();

        await Assert.That(metrics.InFlightCount).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies RecordEnqueue does not throw.
    /// </summary>
    /// <remarks>
    /// <c>RecordEnqueue</c> is a signal for the autoscaling decorator. After the call, the metrics
    /// still implement the interface correctly.
    /// </remarks>
    [Test]
    public async Task RecordEnqueue_DoesNotThrow()
    {
        var metrics = new WorkerMetrics();

        metrics.RecordEnqueue();
        metrics.RecordEnqueue();
        metrics.RecordEnqueue();

        await Assert.That(metrics).IsAssignableTo<IWorkerMetrics>();
    }

    /// <summary>
    /// Verifies RecordExecutionStart increments in-flight count.
    /// </summary>
    [Test]
    public async Task RecordExecutionStart_IncrementsInFlightCount()
    {
        var metrics = new WorkerMetrics();

        metrics.RecordExecutionStart();
        metrics.RecordExecutionStart();

        await Assert.That(metrics.InFlightCount).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies RecordExecutionEnd decrements in-flight count.
    /// </summary>
    [Test]
    public async Task RecordExecutionEnd_DecrementsInFlightCount()
    {
        var metrics = new WorkerMetrics();
        metrics.RecordExecutionStart();
        metrics.RecordExecutionStart();

        metrics.RecordExecutionEnd();

        await Assert.That(metrics.InFlightCount).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies that metrics are thread-safe under concurrent access.
    /// </summary>
    /// <remarks>
    /// The test runs execution start and end operations in parallel. After the balanced operations,
    /// the in-flight count must be zero.
    /// </remarks>
    [Test]
    public async Task Metrics_ThreadSafe_HandlesParallelOperations()
    {
        var metrics = new WorkerMetrics();
        const int operationsPerTask = 1000;
        const int taskCount = 10;

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

        await Assert.That(metrics.InFlightCount).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies interface implementation.
    /// </summary>
    [Test]
    public async Task WorkerMetrics_ImplementsIWorkerMetrics()
    {
        var metrics = new WorkerMetrics();

        await Assert.That(metrics).IsAssignableTo<IWorkerMetrics>();
    }
}
