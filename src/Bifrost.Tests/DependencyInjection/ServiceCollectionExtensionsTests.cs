// =============================================================================
// <copyright file="ServiceCollectionExtensionsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

namespace Bifrost.Tests.DependencyInjection;

/// <summary>
/// Tests for <see cref="ServiceCollectionExtensions"/>.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    /// <summary>
    /// Verifies that AddWorkOrchestrator returns a builder.
    /// </summary>
    [Test]
    public async Task AddWorkOrchestrator_ReturnsBuilder()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());

        var builder = services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 64;
            opts.WorkerCount = 4;
        });

        await Assert.That(builder).IsNotNull();
        await Assert.That(builder).IsTypeOf<WorkOrchestratorBuilder<string>>();
    }

    /// <summary>
    /// Verifies that AddWorkOrchestrator throws when services is null.
    /// </summary>
    [Test]
    public async Task AddWorkOrchestrator_ThrowsWhenServicesNull()
    {
        IServiceCollection services = null!;

        await Assert.That(() => services.AddWorkOrchestrator<string>())
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that AddWorkOrchestrator registers options correctly.
    /// </summary>
    [Test]
    public async Task AddWorkOrchestrator_RegistersOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());

        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 256;
            opts.WorkerCount = 8;
        }).Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        await Assert.That(orchestrator.Capacity).IsEqualTo(256);
        await Assert.That(orchestrator.ActiveWorkers).IsEqualTo(8);

        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }
}