// =============================================================================
// <copyright file="DeadLetterQueueOptionsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.ComponentModel.DataAnnotations;
using System.Reflection;

using Bifrost.Core.DeadLetter;

using TUnit.Core;

namespace Bifrost.Tests.DeadLetter;

/// <summary>
/// Tests for <see cref="DeadLetterQueueOptions"/> configuration.
/// </summary>
[Property("Category", "Unit")]
public class DeadLetterQueueOptionsTests
{
    /// <summary>
    /// Verifies that default values are correct.
    /// </summary>
    [Test]
    public async Task DeadLetterQueueOptions_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var options = new DeadLetterQueueOptions();

        // Assert
        await Assert.That(options.Capacity).IsEqualTo(1000);
        await Assert.That(options.MaxRetries).IsEqualTo(3);
    }

    /// <summary>
    /// Verifies Capacity property has Range attribute.
    /// </summary>
    [Test]
    public async Task Capacity_HasRangeAttribute()
    {
        // Arrange
        var property = typeof(DeadLetterQueueOptions).GetProperty("Capacity");
        var attribute = property?.GetCustomAttribute<RangeAttribute>();

        // Assert
        await Assert.That(attribute).IsNotNull();
        await Assert.That(attribute!.Minimum).IsEqualTo(1);
        await Assert.That(attribute.Maximum).IsEqualTo(int.MaxValue);
    }

    /// <summary>
    /// Verifies MaxRetries property has Range attribute.
    /// </summary>
    [Test]
    public async Task MaxRetries_HasRangeAttribute()
    {
        // Arrange
        var property = typeof(DeadLetterQueueOptions).GetProperty("MaxRetries");
        var attribute = property?.GetCustomAttribute<RangeAttribute>();

        // Assert
        await Assert.That(attribute).IsNotNull();
        await Assert.That(attribute!.Minimum).IsEqualTo(0);
        await Assert.That(attribute.Maximum).IsEqualTo(100);
    }

    /// <summary>
    /// Verifies properties can be set.
    /// </summary>
    [Test]
    public async Task Properties_CanBeSet()
    {
        // Arrange
        var options = new DeadLetterQueueOptions();

        // Act
        options.Capacity = 500;
        options.MaxRetries = 5;

        // Assert
        await Assert.That(options.Capacity).IsEqualTo(500);
        await Assert.That(options.MaxRetries).IsEqualTo(5);
    }

    /// <summary>
    /// Verifies the class is public.
    /// </summary>
    [Test]
    public async Task Class_IsPublic()
    {
        // Arrange
        var type = typeof(DeadLetterQueueOptions);

        // Assert
        await Assert.That(type.IsPublic).IsTrue();
        await Assert.That(type.IsClass).IsTrue();
    }
}
