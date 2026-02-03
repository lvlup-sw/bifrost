// =============================================================================
// <copyright file="HostingExtensionsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Bifrost.Tests.Hosting;

/// <summary>
/// Tests for <see cref="HostingExtensions"/>.
/// </summary>
public class HostingExtensionsTests
{
    /// <summary>
    /// Verifies that AddWorkOrchestratorHostedService registers the hosted service.
    /// </summary>
    [Test]
    public async Task AddWorkOrchestratorHostedService_RegistersHostedService()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkOrchestrator<string>>());

        // Act
        services.AddWorkOrchestratorHostedService<string>();
        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>();

        // Assert
        var hostedServiceTypes = hostedServices.Select(h => h.GetType()).ToList();
        await Assert.That(hostedServiceTypes).Contains(typeof(WorkOrchestratorHostedService<string>));
    }

    /// <summary>
    /// Verifies that AddWorkOrchestratorHostedService returns the service collection for chaining.
    /// </summary>
    [Test]
    public async Task AddWorkOrchestratorHostedService_ReturnsServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var result = services.AddWorkOrchestratorHostedService<string>();

        // Assert
        await Assert.That(result).IsEqualTo(services);
    }

    /// <summary>
    /// Verifies that AddWorkOrchestratorHostedService throws when services is null.
    /// </summary>
    [Test]
    public async Task AddWorkOrchestratorHostedService_ThrowsWhenServicesNull()
    {
        // Arrange
        IServiceCollection services = null!;

        // Act & Assert
        await Assert.That(() => services.AddWorkOrchestratorHostedService<string>())
            .Throws<ArgumentNullException>();
    }
}
