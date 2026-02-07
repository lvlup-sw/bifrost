// =============================================================================
// <copyright file="IWorkHandlerTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;

using Bifrost.Core;

using TUnit.Core;

namespace Bifrost.Tests.Contracts;

/// <summary>
/// Tests for <see cref="IWorkHandler{TWork}"/> interface definition.
/// </summary>
[Property("Category", "Unit")]
public class IWorkHandlerTests
{
    /// <summary>
    /// Verifies that the interface defines HandleAsync method.
    /// </summary>
    [Test]
    public async Task IWorkHandler_Interface_DefinesHandleAsync()
    {
        // Arrange
        var interfaceType = typeof(IWorkHandler<>);

        // Assert - interface exists and is generic
        await Assert.That(interfaceType).IsNotNull();
        await Assert.That(interfaceType.IsInterface).IsTrue();
        await Assert.That(interfaceType.IsGenericTypeDefinition).IsTrue();

        // Check for HandleAsync method
        var method = interfaceType.GetMethod(
            "HandleAsync",
            BindingFlags.Public | BindingFlags.Instance);

        await Assert.That(method).IsNotNull();
    }

    /// <summary>
    /// Verifies HandleAsync method signature.
    /// </summary>
    [Test]
    public async Task HandleAsync_HasCorrectSignature()
    {
        // Arrange
        var interfaceType = typeof(IWorkHandler<>);
        var method = interfaceType.GetMethod("HandleAsync");

        // Assert
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(ValueTask));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(2);
        await Assert.That(parameters[0].Name).IsEqualTo("work");
        await Assert.That(parameters[1].Name).IsEqualTo("ct");
        await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(CancellationToken));
    }

    /// <summary>
    /// Verifies interface is public.
    /// </summary>
    [Test]
    public async Task Interface_IsPublic()
    {
        // Arrange
        var interfaceType = typeof(IWorkHandler<>);

        // Assert
        await Assert.That(interfaceType.IsPublic).IsTrue();
    }
}