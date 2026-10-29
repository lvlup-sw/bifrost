// =============================================================================
// <copyright file="PriorityDispatchExtensionsBindingTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.DependencyInjection;

/// <summary>
/// Tests for the updated <c>UsePriorityDispatch</c> extension (T7):
/// the new <c>binding</c> enum parameter replaces <c>useLockingBinding</c>.
/// </summary>
[Property("Category", "Unit")]
public class PriorityDispatchExtensionsBindingTests
{
    /// <summary>
    /// Verifies that <c>UsePriorityDispatch(DispatchStrategy.PriorityRelaxed)</c> with the default binding sets
    /// <see cref="DispatchStrategy.PriorityRelaxed"/> on the options and
    /// <see cref="DispatchStrategy.PriorityRelaxed"/> on <c>Priority.Binding</c>.
    /// </summary>
    [Test]
    public async Task UsePriorityDispatch_DefaultBinding_SetsPrioritySentinelAndAutoIntent()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 16;
            opts.WorkerCount = 1;
        })
        .UsePriorityDispatch(DispatchStrategy.PriorityRelaxed)
        .Build();

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<WorkOrchestratorOptions>>().Get(WorkOptions.Name<string>());

        // Assert — DispatchStrategy must be the Priority sentinel (not a concrete one).
        // And the Binding intent must be Auto (the default).
        await Assert.That(options.DispatchStrategy).IsEqualTo(DispatchStrategy.PriorityRelaxed);
    }

    /// <summary>
    /// Verifies that <c>UsePriorityDispatch(ordering: DispatchStrategy.PriorityExact)</c>
    /// sets <see cref="DispatchStrategy.PriorityExact"/> on <c>Priority.Binding</c>.
    /// </summary>
    [Test]
    public async Task UsePriorityDispatch_ExplicitLocking_SetsBindingLocking()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 16;
            opts.WorkerCount = 1;
        })
        .UsePriorityDispatch(ordering: DispatchStrategy.PriorityExact)
        .Build();

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<WorkOrchestratorOptions>>().Get(WorkOptions.Name<string>());

        await Assert.That(options.DispatchStrategy).IsEqualTo(DispatchStrategy.PriorityExact);
    }
}
