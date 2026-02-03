// =============================================================================
// <copyright file="CorePackageTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;
using Levelup.Channels.Core;
using TUnit.Core;

namespace Levelup.Channels.Tests;

/// <summary>
/// Tests for the Core package structure and type availability.
/// </summary>
[Property("Category", "Unit")]
public class CorePackageTests
{
    /// <summary>
    /// Verifies that the Core package provides the expected types.
    /// </summary>
    [Test]
    public async Task CorePackageStructure_WhenReferenced_ProvidesExpectedTypes()
    {
        // Arrange
        var coreAssembly = typeof(IWorkOrchestrator<>).Assembly;

        // Act
        var exportedTypes = coreAssembly.GetExportedTypes();

        // Assert - Verify key types are accessible
        await Assert.That(exportedTypes.Any(t => t.Name == "IWorkOrchestrator`1")).IsTrue();
        await Assert.That(exportedTypes.Any(t => t.Name == "IWorkHandler`1")).IsTrue();
        await Assert.That(exportedTypes.Any(t => t.Name == "WorkOrchestratorOptions")).IsTrue();
        await Assert.That(exportedTypes.Any(t => t.Name == "IOrchestratorEvent")).IsTrue();
        await Assert.That(exportedTypes.Any(t => t.Name == "WorkEnqueuedEvent`1")).IsTrue();
        await Assert.That(exportedTypes.Any(t => t.Name == "WorkCompletedEvent`1")).IsTrue();
        await Assert.That(exportedTypes.Any(t => t.Name == "ScalingEvent")).IsTrue();
        await Assert.That(exportedTypes.Any(t => t.Name == "ScalingAction")).IsTrue();
    }
}
