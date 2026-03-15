// =============================================================================
// <copyright file="IDeadLetterSubscriberTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;

using Bifrost.Core.DeadLetter;

using TUnit.Core;

namespace Bifrost.Tests.DeadLetter;

/// <summary>
/// Tests for <see cref="IDeadLetterSubscriber{TWork}"/> interface definition.
/// </summary>
[Property("Category", "Unit")]
public class IDeadLetterSubscriberTests
{
    /// <summary>
    /// Verifies that IDeadLetterSubscriber is an interface.
    /// </summary>
    [Test]
    public async Task IDeadLetterSubscriber_IsInterface()
    {
        // Arrange
        var type = typeof(IDeadLetterSubscriber<>);

        // Assert
        await Assert.That(type.IsInterface).IsTrue();
    }

    /// <summary>
    /// Verifies that IDeadLetterSubscriber is public.
    /// </summary>
    [Test]
    public async Task IDeadLetterSubscriber_IsPublic()
    {
        // Arrange
        var type = typeof(IDeadLetterSubscriber<>);

        // Assert
        await Assert.That(type.IsPublic).IsTrue();
    }

    /// <summary>
    /// Verifies that IDeadLetterSubscriber has a HandleAsync method with the correct signature.
    /// </summary>
    [Test]
    public async Task IDeadLetterSubscriber_HasHandleAsyncMethod()
    {
        // Arrange
        var type = typeof(IDeadLetterSubscriber<string>);
        var method = type.GetMethod("HandleAsync", BindingFlags.Public | BindingFlags.Instance);

        // Assert
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(2);
        await Assert.That(parameters[0].Name).IsEqualTo("item");
        await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(DeadLetteredWork<string>));
        await Assert.That(parameters[1].Name).IsEqualTo("ct");
        await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(CancellationToken));
    }

    /// <summary>
    /// Verifies that IDeadLetterSubscriber is generic with a single type parameter.
    /// </summary>
    [Test]
    public async Task IDeadLetterSubscriber_IsGeneric()
    {
        // Arrange
        var type = typeof(IDeadLetterSubscriber<>);

        // Assert
        await Assert.That(type.IsGenericTypeDefinition).IsTrue();
        await Assert.That(type.GetGenericArguments()).HasCount(1);
    }
}
