// =============================================================================
// <copyright file="MetricsPortTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling.Ports;
using TUnit.Core;

namespace Bifrost.Tests.Autoscaling.Ports;

/// <summary>
/// Tests for <see cref="IAutoscalingMetricsPort"/> interface contract.
/// </summary>
[Property("Category", "Unit")]
public class MetricsPortTests
{
    /// <summary>
    /// Verifies that IAutoscalingMetricsPort defines MaxBacklog property.
    /// </summary>
    [Test]
    public async Task Interface_DefinesMaxBacklogProperty()
    {
        // Arrange
        var type = typeof(IAutoscalingMetricsPort);

        // Act
        var property = type.GetProperty(nameof(IAutoscalingMetricsPort.MaxBacklog));

        // Assert
        await Assert.That(property).IsNotNull();
        await Assert.That(property!.PropertyType).IsEqualTo(typeof(int));
        await Assert.That(property.CanRead).IsTrue();
        await Assert.That(property.CanWrite).IsFalse();
    }

    /// <summary>
    /// Verifies that IAutoscalingMetricsPort defines PendingWorkCount property.
    /// </summary>
    [Test]
    public async Task Interface_DefinesPendingWorkCountProperty()
    {
        // Arrange
        var type = typeof(IAutoscalingMetricsPort);

        // Act
        var property = type.GetProperty(nameof(IAutoscalingMetricsPort.PendingWorkCount));

        // Assert
        await Assert.That(property).IsNotNull();
        await Assert.That(property!.PropertyType).IsEqualTo(typeof(int));
        await Assert.That(property.CanRead).IsTrue();
        await Assert.That(property.CanWrite).IsFalse();
    }

    /// <summary>
    /// Verifies that IAutoscalingMetricsPort defines ActiveWorkerCount property.
    /// </summary>
    [Test]
    public async Task Interface_DefinesActiveWorkerCountProperty()
    {
        // Arrange
        var type = typeof(IAutoscalingMetricsPort);

        // Act
        var property = type.GetProperty(nameof(IAutoscalingMetricsPort.ActiveWorkerCount));

        // Assert
        await Assert.That(property).IsNotNull();
        await Assert.That(property!.PropertyType).IsEqualTo(typeof(int));
        await Assert.That(property.CanRead).IsTrue();
        await Assert.That(property.CanWrite).IsFalse();
    }

    /// <summary>
    /// Verifies that IAutoscalingMetricsPort defines QueuedCount property.
    /// </summary>
    [Test]
    public async Task Interface_DefinesQueuedCountProperty()
    {
        // Arrange
        var type = typeof(IAutoscalingMetricsPort);

        // Act
        var property = type.GetProperty(nameof(IAutoscalingMetricsPort.QueuedCount));

        // Assert
        await Assert.That(property).IsNotNull();
        await Assert.That(property!.PropertyType).IsEqualTo(typeof(long));
        await Assert.That(property.CanRead).IsTrue();
        await Assert.That(property.CanWrite).IsFalse();
    }

    /// <summary>
    /// Verifies that IAutoscalingMetricsPort defines GetUtilizationRatio method.
    /// </summary>
    [Test]
    public async Task Interface_DefinesGetUtilizationRatioMethod()
    {
        // Arrange
        var type = typeof(IAutoscalingMetricsPort);

        // Act
        var method = type.GetMethod(nameof(IAutoscalingMetricsPort.GetUtilizationRatio));

        // Assert
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(double));
        await Assert.That(method.GetParameters()).HasCount().EqualTo(0);
    }

    /// <summary>
    /// Verifies that IAutoscalingMetricsPort is an interface.
    /// </summary>
    [Test]
    public async Task Interface_IsInterface()
    {
        // Arrange
        var type = typeof(IAutoscalingMetricsPort);

        // Assert
        await Assert.That(type.IsInterface).IsTrue();
    }
}
