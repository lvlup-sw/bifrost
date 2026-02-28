// =============================================================================
// <copyright file="IDeadLetterQueueTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;

using Bifrost.Core.DeadLetter;

using TUnit.Core;

namespace Bifrost.Tests.DeadLetter;

/// <summary>
/// Tests for <see cref="IDeadLetterQueue{TWork}"/> interface.
/// </summary>
[Property("Category", "Unit")]
public class IDeadLetterQueueTests
{
    /// <summary>
    /// Verifies that IDeadLetterQueue is an interface.
    /// </summary>
    [Test]
    public async Task IDeadLetterQueue_IsInterface()
    {
        // Arrange
        var type = typeof(IDeadLetterQueue<>);

        // Assert
        await Assert.That(type.IsInterface).IsTrue();
    }

    /// <summary>
    /// Verifies that IDeadLetterQueue has an EnqueueAsync method.
    /// </summary>
    [Test]
    public async Task IDeadLetterQueue_HasEnqueueAsyncMethod()
    {
        // Arrange
        var type = typeof(IDeadLetterQueue<string>);
        var method = type.GetMethod("EnqueueAsync");

        // Assert
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(ValueTask));
    }

    /// <summary>
    /// Verifies that IDeadLetterQueue has a ReadAllAsync method.
    /// </summary>
    [Test]
    public async Task IDeadLetterQueue_HasReadAllAsyncMethod()
    {
        // Arrange
        var type = typeof(IDeadLetterQueue<string>);
        var method = type.GetMethod("ReadAllAsync");

        // Assert
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(IAsyncEnumerable<DeadLetteredWork<string>>));
    }

    /// <summary>
    /// Verifies that IDeadLetterQueue has a Count property.
    /// </summary>
    [Test]
    public async Task IDeadLetterQueue_HasCountProperty()
    {
        // Arrange
        var type = typeof(IDeadLetterQueue<>);
        var property = type.GetProperty("Count");

        // Assert
        await Assert.That(property).IsNotNull();
        await Assert.That(property!.PropertyType).IsEqualTo(typeof(int));
    }

    /// <summary>
    /// Verifies type is public.
    /// </summary>
    [Test]
    public async Task Type_IsPublic()
    {
        // Arrange
        var type = typeof(IDeadLetterQueue<>);

        // Assert
        await Assert.That(type.IsPublic).IsTrue();
    }
}