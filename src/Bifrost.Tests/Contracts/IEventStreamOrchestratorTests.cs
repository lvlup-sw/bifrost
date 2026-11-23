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
        await Assert.That(typeof(IEventStreamOrchestrator<string>)
            .IsAssignableTo(typeof(IWorkOrchestrator<string>))).IsTrue();
    }

    /// <summary>
    /// Verifies that GetEventStreamAsync method exists with correct signature.
    /// </summary>
    [Test]
    public async Task IEventStreamOrchestrator_HasGetEventStreamAsyncMethod()
    {
        var method = typeof(IEventStreamOrchestrator<string>).GetMethod("GetEventStreamAsync");

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
        var method = typeof(IEventStreamOrchestrator<string>).GetMethod("GetEventStreamAsync");
        var genericArgs = method?.GetGenericArguments();

        await Assert.That(genericArgs).IsNotNull();
        await Assert.That(genericArgs!.Length).IsEqualTo(1);

        var constraints = genericArgs[0].GetGenericParameterConstraints();
        await Assert.That(constraints).Contains(typeof(IOrchestratorEvent));
    }

    /// <summary>
    /// Verifies the interface has the expected number of declared-only members.
    /// This catches accidental additions or removals.
    /// </summary>
    /// <remarks>
    /// The interface declares only one member, <c>GetEventStreamAsync</c>. The
    /// <c>DeclaredOnly</c> binding flag excludes inherited members, for example <c>EnqueueAsync</c>
    /// and <c>TryEnqueue</c> from <c>IWorkWriter</c>.
    /// </remarks>
    [Test]
    public async Task Interface_HasExpectedMemberCount()
    {
        var interfaceType = typeof(IEventStreamOrchestrator<>);
        var members = interfaceType.GetMembers(
            BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance);

        await Assert.That(members.Length).IsEqualTo(1);
    }
}
