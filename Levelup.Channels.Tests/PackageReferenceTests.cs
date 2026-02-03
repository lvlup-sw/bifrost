// =============================================================================
// <copyright file="PackageReferenceTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;

namespace Levelup.Channels.Tests;

/// <summary>
/// Tests verifying the Levelup.Channels package references and structure.
/// </summary>
public class PackageReferenceTests
{
    /// <summary>
    /// Verifies that the main Levelup.Channels assembly references Core.
    /// </summary>
    [Test]
    public async Task MainPackage_ReferencesCore()
    {
        // Arrange
        var mainAssembly = typeof(WorkOrchestrator<>).Assembly;
        var referencedAssemblies = mainAssembly.GetReferencedAssemblies();

        // Act
        var coreReference = referencedAssemblies.FirstOrDefault(a =>
            a.Name == "Levelup.Channels.Core");

        // Assert
        await Assert.That(coreReference).IsNotNull();
    }
}
