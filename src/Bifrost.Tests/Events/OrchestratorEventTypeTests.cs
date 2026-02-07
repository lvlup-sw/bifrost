// =============================================================================
// <copyright file="OrchestratorEventTypeTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core.Events;

using TUnit.Core;

namespace Bifrost.Tests.Events;

/// <summary>
/// Tests verifying orchestrator event types are reference types (not value types)
/// to prevent boxing when published through Channel&lt;IOrchestratorEvent&gt;.
/// </summary>
[Property("Category", "Unit")]
public class OrchestratorEventTypeTests
{
    /// <summary>
    /// Verifies WorkEnqueuedEvent is a reference type to avoid boxing.
    /// </summary>
    [Test]
    public async Task WorkEnqueuedEvent_IsReferenceType()
    {
        // Arrange
        var type = typeof(WorkEnqueuedEvent<int>);

        // Assert
        await Assert.That(type.IsValueType).IsFalse();
    }

    /// <summary>
    /// Verifies WorkCompletedEvent is a reference type to avoid boxing.
    /// </summary>
    [Test]
    public async Task WorkCompletedEvent_IsReferenceType()
    {
        // Arrange
        var type = typeof(WorkCompletedEvent<int>);

        // Assert
        await Assert.That(type.IsValueType).IsFalse();
    }

    /// <summary>
    /// Verifies ScalingEvent is a reference type to avoid boxing.
    /// </summary>
    [Test]
    public async Task ScalingEvent_IsReferenceType()
    {
        // Arrange
        var type = typeof(ScalingEvent);

        // Assert
        await Assert.That(type.IsValueType).IsFalse();
    }
}