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
        var type = typeof(IAutoscalingEventsPort);

        var method = type.GetMethod(nameof(IAutoscalingEventsPort.PublishEvent));

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
        var type = typeof(IAutoscalingEventsPort);

        var property = type.GetProperty(nameof(IAutoscalingEventsPort.QueuedEventCount));

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
        var type = typeof(IAutoscalingEventsPort);

        var property = type.GetProperty(nameof(IAutoscalingEventsPort.ActiveSubscriberCount));

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
        var type = typeof(IAutoscalingEventsPort);

        await Assert.That(type.IsInterface).IsTrue();
    }
}