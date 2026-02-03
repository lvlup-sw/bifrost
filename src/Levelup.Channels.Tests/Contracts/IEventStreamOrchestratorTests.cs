// =============================================================================
// <copyright file="IEventStreamOrchestratorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Levelup.Channels.Core;
using Levelup.Channels.Core.Events;

namespace Levelup.Channels.Tests.Contracts;

/// <summary>
/// Tests for <see cref="IEventStreamOrchestrator{TWork}"/> interface contract.
/// </summary>
public class IEventStreamOrchestratorTests
{
    /// <summary>
    /// Verifies that the interface extends IWorkOrchestrator.
    /// </summary>
    [Test]
    public async Task IEventStreamOrchestrator_ExtendsIWorkOrchestrator()
    {
        // Assert
        await Assert.That(typeof(IEventStreamOrchestrator<string>)
            .IsAssignableTo(typeof(IWorkOrchestrator<string>))).IsTrue();
    }

    /// <summary>
    /// Verifies that GetEventStreamAsync method exists with correct signature.
    /// </summary>
    [Test]
    public async Task IEventStreamOrchestrator_HasGetEventStreamAsyncMethod()
    {
        // Arrange
        var method = typeof(IEventStreamOrchestrator<string>).GetMethod("GetEventStreamAsync");

        // Assert
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType.GetGenericTypeDefinition())
            .IsEqualTo(typeof(IAsyncEnumerable<>));
    }

    /// <summary>
    /// Verifies that GetEventStreamAsync has generic type constraint for IOrchestratorEvent.
    /// </summary>
    [Test]
    public async Task IEventStreamOrchestrator_GetEventStreamAsync_HasGenericConstraint()
    {
        // Arrange
        var method = typeof(IEventStreamOrchestrator<string>).GetMethod("GetEventStreamAsync");
        var genericArgs = method?.GetGenericArguments();

        // Assert
        await Assert.That(genericArgs).IsNotNull();
        await Assert.That(genericArgs!.Length).IsEqualTo(1);

        var constraints = genericArgs[0].GetGenericParameterConstraints();
        await Assert.That(constraints).Contains(typeof(IOrchestratorEvent));
    }
}
