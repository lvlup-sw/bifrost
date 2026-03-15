// =============================================================================
// <copyright file="DeadLetterQueueOptionsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.ComponentModel.DataAnnotations;
using System.Reflection;

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.DeadLetter;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

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

    /// <summary>
    /// Clarifies MaxRetries semantics: with MaxRetries=3, total processing attempts = 4.
    /// Formula: total attempts = 1 (initial) + MaxRetries.
    /// This test makes the semantics explicit via DeadLetterHandler behavior.
    /// </summary>
    [Test]
    public async Task MaxRetries_ClarifiesTotalAttempts_FormulaIs1PlusMaxRetries()
    {
        // Arrange
        const int maxRetries = 3;
        const int expectedTotalAttempts = 1 + maxRetries; // 4

        var innerHandler = Substitute.For<IWorkHandler<string>>();
        var dlq = Substitute.For<IDeadLetterQueue<string>>();
        var notifier = new DeadLetterNotifier<string>();
        var options = Options.Create(new DeadLetterQueueOptions { MaxRetries = maxRetries });

        var actualCallCount = 0;
        innerHandler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                actualCallCount++;
                return new ValueTask(Task.FromException(new InvalidOperationException("always fails")));
            });

        var handler = new DeadLetterHandler<string>(
            innerHandler, dlq, notifier, options,
            NullLogger<DeadLetterHandler<string>>.Instance);

        // Act
        await handler.HandleAsync("work", CancellationToken.None).ConfigureAwait(false);

        // Assert - MaxRetries=3 means 1 initial + 3 retries = 4 total attempts
        await Assert.That(actualCallCount).IsEqualTo(expectedTotalAttempts);
    }
}