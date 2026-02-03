// =============================================================================
// <copyright file="WorkCompletedEventTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core.Events;
using TUnit.Core;

namespace Bifrost.Tests.Events;

/// <summary>
/// Tests for <see cref="WorkCompletedEvent{TWork}"/> struct.
/// </summary>
[Property("Category", "Unit")]
public class WorkCompletedEventTests
{
    /// <summary>
    /// Verifies that WorkCompletedEvent is a readonly record struct.
    /// </summary>
    [Test]
    public async Task WorkCompletedEvent_IsReadonlyRecordStruct()
    {
        // Arrange
        var type = typeof(WorkCompletedEvent<>);

        // Assert - check it's a value type (struct)
        await Assert.That(type.IsValueType).IsTrue();

        // Check it's a record (implements IEquatable<T>)
        var interfaces = type.GetInterfaces();
        await Assert.That(interfaces.Any(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IEquatable<>))).IsTrue();
    }

    /// <summary>
    /// Verifies the struct has correct properties.
    /// </summary>
    [Test]
    public async Task WorkCompletedEvent_HasCorrectProperties()
    {
        // Arrange
        var type = typeof(WorkCompletedEvent<>);

        // Assert
        var workProperty = type.GetProperty("Work");
        var durationProperty = type.GetProperty("Duration");
        var successProperty = type.GetProperty("Success");

        await Assert.That(workProperty).IsNotNull();
        await Assert.That(durationProperty).IsNotNull();
        await Assert.That(successProperty).IsNotNull();

        await Assert.That(durationProperty!.PropertyType).IsEqualTo(typeof(TimeSpan));
        await Assert.That(successProperty!.PropertyType).IsEqualTo(typeof(bool));
    }

    /// <summary>
    /// Verifies the struct implements IOrchestratorEvent.
    /// </summary>
    [Test]
    public async Task WorkCompletedEvent_ImplementsIOrchestratorEvent()
    {
        // Arrange
        var type = typeof(WorkCompletedEvent<>);

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
        var duration = TimeSpan.FromMilliseconds(100);
        var success = true;

        // Act
        var evt = new WorkCompletedEvent<string>(work, duration, success);

        // Assert
        await Assert.That(evt.Work).IsEqualTo(work);
        await Assert.That(evt.Duration).IsEqualTo(duration);
        await Assert.That(evt.Success).IsEqualTo(success);
    }

    /// <summary>
    /// Verifies constructor handles failure case.
    /// </summary>
    [Test]
    public async Task Constructor_SetsSuccessFalse_WhenFailed()
    {
        // Arrange
        var work = "test-work";
        var duration = TimeSpan.FromMilliseconds(50);
        var success = false;

        // Act
        var evt = new WorkCompletedEvent<string>(work, duration, success);

        // Assert
        await Assert.That(evt.Success).IsFalse();
    }

    /// <summary>
    /// Verifies type is public.
    /// </summary>
    [Test]
    public async Task Type_IsPublic()
    {
        // Arrange
        var type = typeof(WorkCompletedEvent<>);

        // Assert
        await Assert.That(type.IsPublic).IsTrue();
    }
}
