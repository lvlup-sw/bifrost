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
    /// Verifies that <c>UsePriorityDispatch()</c> with the default binding sets
    /// <see cref="DispatchStrategy.Priority"/> on the options and
    /// <see cref="PriorityBinding.Auto"/> on <c>Priority.Binding</c>.
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
        .UsePriorityDispatch()
        .Build();

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<WorkOrchestratorOptions>>().Value;

        // Assert — DispatchStrategy must be the Priority sentinel (not a concrete one).
        await Assert.That(options.DispatchStrategy).IsEqualTo(DispatchStrategy.Priority);
        // And the Binding intent must be Auto (the default).
        await Assert.That(options.Priority.Binding).IsEqualTo(PriorityBinding.Auto);
    }

    /// <summary>
    /// Verifies that <c>UsePriorityDispatch(binding: PriorityBinding.Locking)</c>
    /// sets <see cref="PriorityBinding.Locking"/> on <c>Priority.Binding</c>.
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
        .UsePriorityDispatch(binding: PriorityBinding.Locking)
        .Build();

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<WorkOrchestratorOptions>>().Value;

        await Assert.That(options.DispatchStrategy).IsEqualTo(DispatchStrategy.Priority);
        await Assert.That(options.Priority.Binding).IsEqualTo(PriorityBinding.Locking);
    }
}
