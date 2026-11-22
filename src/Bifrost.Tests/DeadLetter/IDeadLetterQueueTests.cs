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
        var type = typeof(IDeadLetterQueue<>);

        await Assert.That(type.IsInterface).IsTrue();
    }

    /// <summary>
    /// Verifies that IDeadLetterQueue has an EnqueueAsync method.
    /// </summary>
    [Test]
    public async Task IDeadLetterQueue_HasEnqueueAsyncMethod()
    {
        var type = typeof(IDeadLetterQueue<string>);
        var method = type.GetMethod("EnqueueAsync");

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(ValueTask));
    }

    /// <summary>
    /// Verifies that IDeadLetterQueue has a ReadAllAsync method.
    /// </summary>
    [Test]
    public async Task IDeadLetterQueue_HasReadAllAsyncMethod()
    {
        var type = typeof(IDeadLetterQueue<string>);
        var method = type.GetMethod("ReadAllAsync");

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(IAsyncEnumerable<DeadLetteredWork<string>>));
    }

    /// <summary>
    /// Verifies that IDeadLetterQueue has a Count property.
    /// </summary>
    [Test]
    public async Task IDeadLetterQueue_HasCountProperty()
    {
        var type = typeof(IDeadLetterQueue<>);
        var property = type.GetProperty("Count");

        await Assert.That(property).IsNotNull();
        await Assert.That(property!.PropertyType).IsEqualTo(typeof(int));
    }

    /// <summary>
    /// Verifies type is public.
    /// </summary>
    [Test]
    public async Task Type_IsPublic()
    {
        var type = typeof(IDeadLetterQueue<>);

        await Assert.That(type.IsPublic).IsTrue();
    }
}