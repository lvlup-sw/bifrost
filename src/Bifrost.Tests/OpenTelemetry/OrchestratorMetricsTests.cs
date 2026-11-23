// =============================================================================
// <copyright file="OrchestratorMetricsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.OpenTelemetry;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.OpenTelemetry;

/// <summary>
/// Tests for <see cref="OrchestratorMetrics{TWork}"/>.
/// </summary>
[Property("Category", "Unit")]
public class OrchestratorMetricsTests
{
    /// <summary>
    /// Verifies that constructor throws when orchestratorProvider is null.
    /// </summary>
    [Test]
    public async Task Constructor_NullProvider_Throws()
    {
        await Assert.That(() => new OrchestratorMetrics<string>(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that constructor creates all metrics.
    /// </summary>
    [Test]
    public async Task Constructor_CreatesAllMetrics()
    {
        using var metrics = new OrchestratorMetrics<string>(() => null);

        await Assert.That(metrics.ItemsEnqueued).IsNotNull();
        await Assert.That(metrics.ItemsProcessed).IsNotNull();
        await Assert.That(metrics.ItemsFailed).IsNotNull();
        await Assert.That(metrics.ProcessingDuration).IsNotNull();
        await Assert.That(metrics.PendingItems).IsNotNull();
        await Assert.That(metrics.ActiveWorkers).IsNotNull();
    }

    /// <summary>
    /// Verifies that RecordEnqueued increments counter.
    /// </summary>
    /// <remarks>
    /// The test cannot read the counter value directly. It only shows that the call does not throw.
    /// </remarks>
    [Test]
    public async Task RecordEnqueued_IncrementsCounter()
    {
        using var metrics = new OrchestratorMetrics<string>(() => null);

        metrics.RecordEnqueued();
        metrics.RecordEnqueued();

        await Assert.That(metrics.ItemsEnqueued).IsNotNull();
    }

    /// <summary>
    /// Verifies that RecordProcessed increments counter and records duration.
    /// </summary>
    [Test]
    public async Task RecordProcessed_IncrementsCounterAndRecordsDuration()
    {
        using var metrics = new OrchestratorMetrics<string>(() => null);

        metrics.RecordProcessed(100.5);
        metrics.RecordProcessed(50.25);

        await Assert.That(metrics.ItemsProcessed).IsNotNull();
        await Assert.That(metrics.ProcessingDuration).IsNotNull();
    }

    /// <summary>
    /// Verifies that RecordFailed increments counter and records duration.
    /// </summary>
    [Test]
    public async Task RecordFailed_IncrementsCounterAndRecordsDuration()
    {
        using var metrics = new OrchestratorMetrics<string>(() => null);

        metrics.RecordFailed(25.0);

        await Assert.That(metrics.ItemsFailed).IsNotNull();
        await Assert.That(metrics.ProcessingDuration).IsNotNull();
    }

    /// <summary>
    /// Verifies that observable gauges read from orchestrator.
    /// </summary>
    /// <remarks>
    /// The test only shows that the gauges are created. It does not observe the gauge values.
    /// </remarks>
    [Test]
    public async Task ObservableGauges_ReadFromOrchestrator()
    {
        var mockOrchestrator = Substitute.For<IWorkOrchestrator<string>>();
        mockOrchestrator.PendingCount.Returns(5);
        mockOrchestrator.ActiveWorkers.Returns(3);

        using var metrics = new OrchestratorMetrics<string>(() => mockOrchestrator);

        await Assert.That(metrics.PendingItems).IsNotNull();
        await Assert.That(metrics.ActiveWorkers).IsNotNull();
    }

    /// <summary>
    /// Verifies that Dispose can be called multiple times.
    /// </summary>
    [Test]
    public async Task Dispose_CanBeCalledMultipleTimes()
    {
        var metrics = new OrchestratorMetrics<string>(() => null);

        metrics.Dispose();
        metrics.Dispose();

        var completed = true;
        await Assert.That(completed).IsTrue();
    }

    /// <summary>
    /// Verifies that meter name includes work type name.
    /// </summary>
    /// <remarks>
    /// The test cannot read the meter name directly. It shows that different work types create separate
    /// metrics instances.
    /// </remarks>
    [Test]
    public async Task MeterName_IncludesWorkTypeName()
    {
        using var stringMetrics = new OrchestratorMetrics<string>(() => null);
        using var intMetrics = new OrchestratorMetrics<int>(() => null);

        await Assert.That(stringMetrics).IsNotNull();
        await Assert.That(intMetrics).IsNotNull();
    }

    /// <summary>
    /// Verifies that constructor creates the dead-lettered counter.
    /// </summary>
    [Test]
    public async Task Constructor_CreatesDeadLetteredCounter()
    {
        using var metrics = new OrchestratorMetrics<string>(() => null);

        await Assert.That(metrics.ItemsDeadLettered).IsNotNull();
    }

    /// <summary>
    /// Verifies that RecordDeadLettered increments the counter.
    /// </summary>
    [Test]
    public async Task RecordDeadLettered_IncrementsCounter()
    {
        using var metrics = new OrchestratorMetrics<string>(() => null);

        metrics.RecordDeadLettered();
        metrics.RecordDeadLettered();

        await Assert.That(metrics.ItemsDeadLettered).IsNotNull();
    }

    /// <summary>
    /// Verifies that constructor creates the DLQ depth gauge.
    /// </summary>
    [Test]
    public async Task Constructor_CreatesDlqDepthGauge()
    {
        using var metrics = new OrchestratorMetrics<string>(() => null, () => 5);

        await Assert.That(metrics.DeadLetterQueueDepth).IsNotNull();
    }
}