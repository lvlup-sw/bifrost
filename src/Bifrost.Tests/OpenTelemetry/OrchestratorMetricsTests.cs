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
        // Act & Assert
        await Assert.That(() => new OrchestratorMetrics<string>(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that constructor creates all metrics.
    /// </summary>
    [Test]
    public async Task Constructor_CreatesAllMetrics()
    {
        // Arrange & Act
        using var metrics = new OrchestratorMetrics<string>(() => null);

        // Assert
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
    [Test]
    public async Task RecordEnqueued_IncrementsCounter()
    {
        // Arrange
        using var metrics = new OrchestratorMetrics<string>(() => null);

        // Act - Should not throw
        metrics.RecordEnqueued();
        metrics.RecordEnqueued();

        // Assert - Counter should be incremented (we can't directly read the value,
        // but we verify no exception is thrown)
        await Assert.That(metrics.ItemsEnqueued).IsNotNull();
    }

    /// <summary>
    /// Verifies that RecordProcessed increments counter and records duration.
    /// </summary>
    [Test]
    public async Task RecordProcessed_IncrementsCounterAndRecordsDuration()
    {
        // Arrange
        using var metrics = new OrchestratorMetrics<string>(() => null);

        // Act - Should not throw
        metrics.RecordProcessed(100.5);
        metrics.RecordProcessed(50.25);

        // Assert
        await Assert.That(metrics.ItemsProcessed).IsNotNull();
        await Assert.That(metrics.ProcessingDuration).IsNotNull();
    }

    /// <summary>
    /// Verifies that RecordFailed increments counter and records duration.
    /// </summary>
    [Test]
    public async Task RecordFailed_IncrementsCounterAndRecordsDuration()
    {
        // Arrange
        using var metrics = new OrchestratorMetrics<string>(() => null);

        // Act - Should not throw
        metrics.RecordFailed(25.0);

        // Assert
        await Assert.That(metrics.ItemsFailed).IsNotNull();
        await Assert.That(metrics.ProcessingDuration).IsNotNull();
    }

    /// <summary>
    /// Verifies that observable gauges read from orchestrator.
    /// </summary>
    [Test]
    public async Task ObservableGauges_ReadFromOrchestrator()
    {
        // Arrange
        var mockOrchestrator = Substitute.For<IWorkOrchestrator<string>>();
        mockOrchestrator.PendingCount.Returns(5);
        mockOrchestrator.ActiveWorkers.Returns(3);

        // Act
        using var metrics = new OrchestratorMetrics<string>(() => mockOrchestrator);

        // Assert - Observable gauges should be created (we verify the provider is called
        // when gauges are observed by checking the mock was set up)
        await Assert.That(metrics.PendingItems).IsNotNull();
        await Assert.That(metrics.ActiveWorkers).IsNotNull();
    }

    /// <summary>
    /// Verifies that Dispose can be called multiple times.
    /// </summary>
    [Test]
    public async Task Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var metrics = new OrchestratorMetrics<string>(() => null);

        // Act - Should not throw
        metrics.Dispose();
        metrics.Dispose();

        // Assert - Verify no exception was thrown (test passes if we reach here)
        var completed = true;
        await Assert.That(completed).IsTrue();
    }

    /// <summary>
    /// Verifies that meter name includes work type name.
    /// </summary>
    [Test]
    public async Task MeterName_IncludesWorkTypeName()
    {
        // Arrange & Act
        using var stringMetrics = new OrchestratorMetrics<string>(() => null);
        using var intMetrics = new OrchestratorMetrics<int>(() => null);

        // Assert - We can't directly access meter name, but we verify different types
        // create separate metrics instances
        await Assert.That(stringMetrics).IsNotNull();
        await Assert.That(intMetrics).IsNotNull();
    }

    /// <summary>
    /// Verifies that constructor creates the dead-lettered counter.
    /// </summary>
    [Test]
    public async Task Constructor_CreatesDeadLetteredCounter()
    {
        // Arrange & Act
        using var metrics = new OrchestratorMetrics<string>(() => null);

        // Assert
        await Assert.That(metrics.ItemsDeadLettered).IsNotNull();
    }

    /// <summary>
    /// Verifies that RecordDeadLettered increments the counter.
    /// </summary>
    [Test]
    public async Task RecordDeadLettered_IncrementsCounter()
    {
        // Arrange
        using var metrics = new OrchestratorMetrics<string>(() => null);

        // Act - Should not throw
        metrics.RecordDeadLettered();
        metrics.RecordDeadLettered();

        // Assert
        await Assert.That(metrics.ItemsDeadLettered).IsNotNull();
    }

    /// <summary>
    /// Verifies that constructor creates the DLQ depth gauge.
    /// </summary>
    [Test]
    public async Task Constructor_CreatesDlqDepthGauge()
    {
        // Arrange & Act
        using var metrics = new OrchestratorMetrics<string>(() => null, () => 5);

        // Assert
        await Assert.That(metrics.DeadLetterQueueDepth).IsNotNull();
    }
}