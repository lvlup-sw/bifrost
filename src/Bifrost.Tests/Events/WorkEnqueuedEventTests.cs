// =============================================================================
// <copyright file="WorkEnqueuedEventTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;

using Bifrost.Core.Events;

using TUnit.Core;

namespace Bifrost.Tests.Events;

/// <summary>
/// Tests for <see cref="WorkEnqueuedEvent{TWork}"/> sealed record class.
/// </summary>
[Property("Category", "Unit")]
public class WorkEnqueuedEventTests
{
    /// <summary>
    /// Verifies that WorkEnqueuedEvent is a sealed record class.
    /// </summary>
    [Test]
    public async Task WorkEnqueuedEvent_IsSealedRecordClass()
    {
        // Arrange
        var type = typeof(WorkEnqueuedEvent<>);

        // Assert - check it's a reference type (class), not a value type (struct)
        await Assert.That(type.IsValueType).IsFalse();

        // Check it's a record (has EqualityContract property or similar record patterns)
        // Records implement IEquatable<T>
        var interfaces = type.GetInterfaces();
        await Assert.That(interfaces.Any(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IEquatable<>))).IsTrue();
    }

    /// <summary>
    /// Verifies the record class has correct properties.
    /// </summary>
    [Test]
    public async Task WorkEnqueuedEvent_HasCorrectProperties()
    {
        // Arrange
        var type = typeof(WorkEnqueuedEvent<>);

        // Assert
        var workProperty = type.GetProperty("Work");
        var timestampProperty = type.GetProperty("Timestamp");
        var queueDepthProperty = type.GetProperty("QueueDepth");

        await Assert.That(workProperty).IsNotNull();
        await Assert.That(timestampProperty).IsNotNull();
        await Assert.That(queueDepthProperty).IsNotNull();

        await Assert.That(timestampProperty!.PropertyType).IsEqualTo(typeof(DateTimeOffset));
        await Assert.That(queueDepthProperty!.PropertyType).IsEqualTo(typeof(int));
    }

    /// <summary>
    /// Verifies the record class implements IOrchestratorEvent.
    /// </summary>
    [Test]
    public async Task WorkEnqueuedEvent_ImplementsIOrchestratorEvent()
    {
        // Arrange
        var type = typeof(WorkEnqueuedEvent<>);

        // Assert
        await Assert.That(typeof(IOrchestratorEvent).IsAssignableFrom(type)).IsTrue();
    }

    /// <summary>
    /// Verifies constructor creates instance with correct values.
    /// </summary>
    [Test]
    public async Task Constructor_SetsPropertiesCorrectly()
    {
        // Arrange
        var work = "test-work";
        var timestamp = DateTimeOffset.UtcNow;
        var queueDepth = 5;

        // Act
        var evt = new WorkEnqueuedEvent<string>(work, timestamp, queueDepth);

        // Assert
        await Assert.That(evt.Work).IsEqualTo(work);
        await Assert.That(evt.Timestamp).IsEqualTo(timestamp);
        await Assert.That(evt.QueueDepth).IsEqualTo(queueDepth);
    }

    /// <summary>
    /// Verifies type is public.
    /// </summary>
    [Test]
    public async Task Type_IsPublic()
    {
        // Arrange
        var type = typeof(WorkEnqueuedEvent<>);

        // Assert
        await Assert.That(type.IsPublic).IsTrue();
    }
}