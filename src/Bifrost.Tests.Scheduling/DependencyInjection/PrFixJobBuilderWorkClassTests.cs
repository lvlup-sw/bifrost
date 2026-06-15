// =============================================================================
// <copyright file="PrFixJobBuilderWorkClassTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.DependencyInjection;
using Bifrost.Scheduling.Dispatch;

using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Tests.Scheduling.DependencyInjection;

/// <summary>
/// Regression tests for PR #25 CodeRabbit FIX C4: switching to custom dispatch via
/// <see cref="IJobBuilder{TWork}.DispatchVia{TDispatcher}"/> must reset a work class
/// set by an earlier <see cref="IJobBuilder{TWork}.DispatchTo{TOrchestrator}"/>. The
/// work class is meaningful only for orchestrator dispatch; for custom dispatch it
/// is unused and must read as the neutral default rather than a stale value leaked
/// from the overridden orchestrator configuration.
/// </summary>
public sealed class PrFixJobBuilderWorkClassTests
{
    /// <summary>
    /// Verifies that calling <c>.DispatchTo(..., WorkClass.Interactive)</c> and then
    /// overriding it with <c>.DispatchVia&lt;TDispatcher&gt;()</c> yields a definition
    /// whose work class is the neutral default, not the stale <c>Interactive</c>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchVia_AfterDispatchTo_ResetsWorkClassToDefault()
    {
        var builder = new SchedulerBuilder(new ServiceCollection());
        builder.AddJob<TestWork>("override-job")
            .Every(TimeSpan.FromMinutes(5))
            .DispatchTo<IWorkOrchestrator<TestWork>>(_ => new TestWork(), WorkClass.Interactive)
            .DispatchVia<CustomDispatcher>();

        var definition = builder.Definitions.Single();

        await Assert.That(definition.DispatchKind).IsEqualTo("custom");
        await Assert.That(definition.WorkClass).IsEqualTo(WorkClass.Default);
        await Assert.That(definition.WorkClass).IsNotEqualTo(WorkClass.Interactive);
    }

    private sealed class TestWork;

    private sealed class CustomDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
            => ValueTask.CompletedTask;
    }
}
