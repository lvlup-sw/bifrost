// =============================================================================
// <copyright file="DoubleAddSchedulerTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Tests.Scheduling.DependencyInjection;

/// <summary>
/// Tests for the double-<c>AddScheduler</c> guard (Task 51, R10/NCronJob#138):
/// calling <see cref="SchedulerServiceCollectionExtensions.AddScheduler"/> more than
/// once on the same <see cref="IServiceCollection"/> throws
/// <see cref="InvalidOperationException"/>, because silently no-oping looks
/// composable but hides configuration conflicts and undefined behavior.
/// </summary>
public sealed class DoubleAddSchedulerTests
{
    /// <summary>
    /// Verifies that calling <c>AddScheduler</c> twice on the same
    /// <see cref="IServiceCollection"/> throws <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AddScheduler_CalledTwice_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();
        services.AddScheduler();

        await Assert.That(() => services.AddScheduler())
            .Throws<InvalidOperationException>();
    }
}
