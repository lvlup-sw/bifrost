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
        var type = typeof(IAutoscalingMetricsPort);

        var property = type.GetProperty(nameof(IAutoscalingMetricsPort.MaxBacklog));

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
        var type = typeof(IAutoscalingMetricsPort);

        var property = type.GetProperty(nameof(IAutoscalingMetricsPort.PendingWorkCount));

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
        var type = typeof(IAutoscalingMetricsPort);

        var property = type.GetProperty(nameof(IAutoscalingMetricsPort.ActiveWorkerCount));

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
        var type = typeof(IAutoscalingMetricsPort);

        var property = type.GetProperty(nameof(IAutoscalingMetricsPort.QueuedCount));

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
        var type = typeof(IAutoscalingMetricsPort);

        var method = type.GetMethod(nameof(IAutoscalingMetricsPort.GetUtilizationRatio));

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
        var type = typeof(IAutoscalingMetricsPort);

        await Assert.That(type.IsInterface).IsTrue();
    }
}