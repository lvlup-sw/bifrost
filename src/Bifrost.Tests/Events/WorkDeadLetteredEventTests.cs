// =============================================================================
// <copyright file="WorkDeadLetteredEventTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core.Events;

using TUnit.Core;

namespace Bifrost.Tests.Events;

/// <summary>
/// Tests for <see cref="WorkDeadLetteredEvent{TWork}"/> sealed record class.
/// </summary>
[Property("Category", "Unit")]
public class WorkDeadLetteredEventTests
{
    /// <summary>
    /// Verifies that WorkDeadLetteredEvent is a sealed record class.
    /// </summary>
    [Test]
    public async Task WorkDeadLetteredEvent_IsSealedRecordClass()
    {
        // Arrange
        var type = typeof(WorkDeadLetteredEvent<>);

        // Assert - check it's a reference type (class), not a value type (struct)
        await Assert.That(type.IsValueType).IsFalse();

        // Check it's a record (has IEquatable<T>)
        var interfaces = type.GetInterfaces();
        await Assert.That(interfaces.Any(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IEquatable<>))).IsTrue();
    }

    /// <summary>
    /// Verifies the record class has correct properties.
    /// </summary>
    [Test]
    public async Task WorkDeadLetteredEvent_HasCorrectProperties()
    {
        // Arrange
        var type = typeof(WorkDeadLetteredEvent<>);

        // Assert
        var workProperty = type.GetProperty("Work");
        var exceptionProperty = type.GetProperty("Exception");
        var attemptCountProperty = type.GetProperty("AttemptCount");
        var timestampProperty = type.GetProperty("Timestamp");
        var correlationIdProperty = type.GetProperty("CorrelationId");

        await Assert.That(workProperty).IsNotNull();
        await Assert.That(exceptionProperty).IsNotNull();
        await Assert.That(attemptCountProperty).IsNotNull();
        await Assert.That(timestampProperty).IsNotNull();
        await Assert.That(correlationIdProperty).IsNotNull();

        await Assert.That(exceptionProperty!.PropertyType).IsEqualTo(typeof(Exception));
        await Assert.That(attemptCountProperty!.PropertyType).IsEqualTo(typeof(int));
        await Assert.That(timestampProperty!.PropertyType).IsEqualTo(typeof(DateTimeOffset));
        await Assert.That(correlationIdProperty!.PropertyType).IsEqualTo(typeof(string));
    }

    /// <summary>
    /// Verifies the record class implements IOrchestratorEvent.
    /// </summary>
    [Test]
    public async Task WorkDeadLetteredEvent_ImplementsIOrchestratorEvent()
    {
        // Arrange
        var type = typeof(WorkDeadLetteredEvent<>);

        // Assert
        await Assert.That(typeof(IOrchestratorEvent).IsAssignableFrom(type)).IsTrue();
    }

    /// <summary>
    /// Verifies the record class implements ICorrelatedEvent.
    /// </summary>
    [Test]
    public async Task WorkDeadLetteredEvent_ImplementsICorrelatedEvent()
    {
        // Arrange
        var type = typeof(WorkDeadLetteredEvent<>);

        // Assert
        await Assert.That(typeof(ICorrelatedEvent).IsAssignableFrom(type)).IsTrue();
    }

    /// <summary>
    /// Verifies constructor creates instance with correct values.
    /// </summary>
    [Test]
    public async Task Constructor_SetsPropertiesCorrectly()
    {
        // Arrange
        var work = "test-work";
        var exception = new InvalidOperationException("test");
        var attemptCount = 3;
        var timestamp = DateTimeOffset.UtcNow;
        var correlationId = "corr-123";

        // Act
        var evt = new WorkDeadLetteredEvent<string>(work, exception, attemptCount, timestamp, correlationId);

        // Assert
        await Assert.That(evt.Work).IsEqualTo(work);
        await Assert.That(evt.Exception).IsEqualTo(exception);
        await Assert.That(evt.AttemptCount).IsEqualTo(attemptCount);
        await Assert.That(evt.Timestamp).IsEqualTo(timestamp);
        await Assert.That(evt.CorrelationId).IsEqualTo(correlationId);
    }

    /// <summary>
    /// Verifies type is public.
    /// </summary>
    [Test]
    public async Task Type_IsPublic()
    {
        // Arrange
        var type = typeof(WorkDeadLetteredEvent<>);

        // Assert
        await Assert.That(type.IsPublic).IsTrue();
    }
}