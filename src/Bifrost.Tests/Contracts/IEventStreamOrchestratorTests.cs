// =============================================================================
// <copyright file="IEventStreamOrchestratorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;

using Bifrost.Core;
using Bifrost.Core.Events;

using TUnit.Core;

namespace Bifrost.Tests.Contracts;

/// <summary>
/// Tests for <see cref="IEventStreamOrchestrator{TWork}"/> interface contract.
/// </summary>
[Property("Category", "Unit")]
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

    /// <summary>
    /// Verifies the interface has the expected number of declared-only members.
    /// This catches accidental additions or removals.
    /// </summary>
    [Test]
    public async Task Interface_HasExpectedMemberCount()
    {
        // Arrange
        var interfaceType = typeof(IEventStreamOrchestrator<>);
        var members = interfaceType.GetMembers(
            BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance);

        // Assert - 3 declared methods: GetEventStreamAsync, EnqueueAsync, TryEnqueue
        await Assert.That(members.Length).IsEqualTo(1);
    }
}
