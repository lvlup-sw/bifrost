// =============================================================================
// <copyright file="IOrchestratorEventTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Levelup.Channels.Core.Events;
using TUnit.Core;

namespace Levelup.Channels.Tests.Events;

/// <summary>
/// Unit tests for the <see cref="IOrchestratorEvent"/> marker interface.
/// </summary>
/// <remarks>
/// Tests cover interface type characteristics including visibility and member constraints.
/// </remarks>
[Property("Category", "Unit")]
public class IOrchestratorEventTests
{
    /// <summary>
    /// Verifies that IOrchestratorEvent is a marker interface with no members.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges the interface type via reflection, asserts it has no methods or properties.
    /// Marker interfaces are used for type tagging without requiring implementation.
    /// </remarks>
    [Test]
    public async Task IOrchestratorEvent_IsMarkerInterface()
    {
        // Arrange
        var interfaceType = typeof(IOrchestratorEvent);

        // Assert - should be an interface with no members
        await Assert.That(interfaceType.IsInterface).IsTrue();

        var methods = interfaceType.GetMethods();
        var properties = interfaceType.GetProperties();

        await Assert.That(methods).IsEmpty();
        await Assert.That(properties).IsEmpty();
    }

    /// <summary>
    /// Verifies that the IOrchestratorEvent interface is publicly accessible.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges the interface type via reflection, asserts the IsPublic flag is true.
    /// Public visibility is required for cross-assembly event handling.
    /// </remarks>
    [Test]
    public async Task Interface_IsPublic()
    {
        // Arrange
        var interfaceType = typeof(IOrchestratorEvent);

        // Assert
        await Assert.That(interfaceType.IsPublic).IsTrue();
    }
}
