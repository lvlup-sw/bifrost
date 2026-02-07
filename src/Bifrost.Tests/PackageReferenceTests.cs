// =============================================================================
// <copyright file="PackageReferenceTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;

namespace Bifrost.Tests;

/// <summary>
/// Tests verifying the Bifrost package references and structure.
/// </summary>
public class PackageReferenceTests
{
    /// <summary>
    /// Verifies that the main Bifrost assembly references Core.
    /// </summary>
    [Test]
    public async Task MainPackage_ReferencesCore()
    {
        // Arrange
        var mainAssembly = typeof(WorkOrchestrator<>).Assembly;
        var referencedAssemblies = mainAssembly.GetReferencedAssemblies();

        // Act
        var coreReference = referencedAssemblies.FirstOrDefault(a =>
            a.Name == "Bifrost.Core");

        // Assert
        await Assert.That(coreReference).IsNotNull();
    }
}