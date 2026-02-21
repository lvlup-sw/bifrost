// =============================================================================
// <copyright file="DeadLetteredWorkTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core.DeadLetter;

using TUnit.Core;

namespace Bifrost.Tests.DeadLetter;

/// <summary>
/// Tests for <see cref="DeadLetteredWork{TWork}"/> readonly record struct.
/// </summary>
[Property("Category", "Unit")]
public class DeadLetteredWorkTests
{
    /// <summary>
    /// Verifies that DeadLetteredWork is a value type (struct).
    /// </summary>
    [Test]
    public async Task DeadLetteredWork_IsValueType()
    {
        // Arrange
        var type = typeof(DeadLetteredWork<>);

        // Assert
        await Assert.That(type.IsValueType).IsTrue();
    }

    /// <summary>
    /// Verifies that DeadLetteredWork has the correct properties.
    /// </summary>
    [Test]
    public async Task DeadLetteredWork_HasCorrectProperties()
    {
        // Arrange
        var type = typeof(DeadLetteredWork<>);

        // Assert
        var workProperty = type.GetProperty("Work");
        var exceptionProperty = type.GetProperty("Exception");
        var attemptCountProperty = type.GetProperty("AttemptCount");
        var failedAtProperty = type.GetProperty("FailedAt");
        var correlationIdProperty = type.GetProperty("CorrelationId");

        await Assert.That(workProperty).IsNotNull();
        await Assert.That(exceptionProperty).IsNotNull();
        await Assert.That(attemptCountProperty).IsNotNull();
        await Assert.That(failedAtProperty).IsNotNull();
        await Assert.That(correlationIdProperty).IsNotNull();

        await Assert.That(exceptionProperty!.PropertyType).IsEqualTo(typeof(Exception));
        await Assert.That(attemptCountProperty!.PropertyType).IsEqualTo(typeof(int));
        await Assert.That(failedAtProperty!.PropertyType).IsEqualTo(typeof(DateTimeOffset));
        await Assert.That(correlationIdProperty!.PropertyType).IsEqualTo(typeof(string));
    }

    /// <summary>
    /// Verifies that the constructor sets properties correctly.
    /// </summary>
    [Test]
    public async Task Constructor_SetsPropertiesCorrectly()
    {
        // Arrange
        var work = "test-work";
        var exception = new InvalidOperationException("test");
        var attemptCount = 3;
        var failedAt = DateTimeOffset.UtcNow;
        var correlationId = "corr-123";

        // Act
        var item = new DeadLetteredWork<string>(work, exception, attemptCount, failedAt, correlationId);

        // Assert
        await Assert.That(item.Work).IsEqualTo(work);
        await Assert.That(item.Exception).IsEqualTo(exception);
        await Assert.That(item.AttemptCount).IsEqualTo(attemptCount);
        await Assert.That(item.FailedAt).IsEqualTo(failedAt);
        await Assert.That(item.CorrelationId).IsEqualTo(correlationId);
    }

    /// <summary>
    /// Verifies type is public.
    /// </summary>
    [Test]
    public async Task Type_IsPublic()
    {
        // Arrange
        var type = typeof(DeadLetteredWork<>);

        // Assert
        await Assert.That(type.IsPublic).IsTrue();
    }

    /// <summary>
    /// Verifies that DeadLetteredWork implements IEquatable.
    /// </summary>
    [Test]
    public async Task DeadLetteredWork_ImplementsIEquatable()
    {
        // Arrange
        var type = typeof(DeadLetteredWork<string>);
        var interfaces = type.GetInterfaces();

        // Assert
        await Assert.That(interfaces.Any(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IEquatable<>))).IsTrue();
    }
}
