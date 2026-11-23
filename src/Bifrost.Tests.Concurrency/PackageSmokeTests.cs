// =============================================================================
// <copyright file="PackageSmokeTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Smoke tests verifying the Bifrost.Concurrency package scaffold builds and
/// its namespace resolves from the test project.
/// </summary>
public class PackageSmokeTests
{
    /// <summary>
    /// Verifies the Bifrost.Concurrency assembly is reachable at typeof level
    /// and carries the expected assembly identity.
    /// </summary>
    [Test]
    public async Task PackageSmoke_Builds_NamespaceResolves()
    {
        var assembly = typeof(AssemblyMarker).Assembly;

        var assemblyName = assembly.GetName().Name;

        await Assert.That(assemblyName).IsEqualTo("Bifrost.Concurrency");
    }
}
