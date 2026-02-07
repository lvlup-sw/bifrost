// =============================================================================
// <copyright file="EventsPortTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling.Ports;
using Bifrost.Core.Events;

using TUnit.Core;

namespace Bifrost.Tests.Autoscaling.Ports;

/// <summary>
/// Tests for <see cref="IAutoscalingEventsPort"/> interface contract.
/// </summary>
[Property("Category", "Unit")]
public class EventsPortTests
{
    /// <summary>
    /// Verifies that IAutoscalingEventsPort defines PublishEvent method.
    /// </summary>
    [Test]
    public async Task Interface_DefinesPublishEventMethod()
    {
        // Arrange
        var type = typeof(IAutoscalingEventsPort);

        // Act
        var method = type.GetMethod(nameof(IAutoscalingEventsPort.PublishEvent));

        // Assert
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(void));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount().EqualTo(1);
        await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(IOrchestratorEvent));
        await Assert.That(parameters[0].Name).IsEqualTo("orchestratorEvent");
    }

    /// <summary>
    /// Verifies that IAutoscalingEventsPort defines QueuedEventCount property.
    /// </summary>
    [Test]
    public async Task Interface_DefinesQueuedEventCountProperty()
    {
        // Arrange
        var type = typeof(IAutoscalingEventsPort);

        // Act
        var property = type.GetProperty(nameof(IAutoscalingEventsPort.QueuedEventCount));

        // Assert
        await Assert.That(property).IsNotNull();
        await Assert.That(property!.PropertyType).IsEqualTo(typeof(long));
        await Assert.That(property.CanRead).IsTrue();
        await Assert.That(property.CanWrite).IsFalse();
    }

    /// <summary>
    /// Verifies that IAutoscalingEventsPort defines ActiveSubscriberCount property.
    /// </summary>
    [Test]
    public async Task Interface_DefinesActiveSubscriberCountProperty()
    {
        // Arrange
        var type = typeof(IAutoscalingEventsPort);

        // Act
        var property = type.GetProperty(nameof(IAutoscalingEventsPort.ActiveSubscriberCount));

        // Assert
        await Assert.That(property).IsNotNull();
        await Assert.That(property!.PropertyType).IsEqualTo(typeof(long));
        await Assert.That(property.CanRead).IsTrue();
        await Assert.That(property.CanWrite).IsFalse();
    }

    /// <summary>
    /// Verifies that IAutoscalingEventsPort is an interface.
    /// </summary>
    [Test]
    public async Task Interface_IsInterface()
    {
        // Arrange
        var type = typeof(IAutoscalingEventsPort);

        // Assert
        await Assert.That(type.IsInterface).IsTrue();
    }
}