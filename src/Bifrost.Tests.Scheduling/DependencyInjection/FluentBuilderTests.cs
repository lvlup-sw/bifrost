// =============================================================================
// <copyright file="FluentBuilderTests.cs" company="Levelup Software">
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
/// Tests for the fluent scheduler builder DSL (Task 38, DR-1/DR-2/DR-3/DR-4): the
/// cadence and dispatch fluent methods build a registrable
/// <see cref="JobDefinition"/> carrying the job's name, cadence, missed-fire
/// policy, dispatch kind, and work class, plus a dispatcher factory that resolves
/// the concrete <see cref="IJobDispatcher"/> against an
/// <see cref="IServiceProvider"/>.
/// </summary>
public sealed class FluentBuilderTests
{
    private static readonly DateTimeOffset At =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies <c>.Every(TimeSpan)</c> builds an <see cref="IntervalCadence"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Every_BuildsIntervalCadence()
    {
        var builder = NewBuilder();
        builder.AddJob<TestWork>("interval-job")
            .Every(TimeSpan.FromMinutes(5))
            .DispatchTo<IWorkOrchestrator<TestWork>>(_ => new TestWork());

        var definition = SingleDefinition(builder);

        await Assert.That(definition.Name).IsEqualTo("interval-job");
        var interval = definition.Cadence as IntervalCadence;
        await Assert.That(interval).IsNotNull();
        await Assert.That(interval!.Interval).IsEqualTo(TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Verifies <c>.Cron(expr)</c> builds a <see cref="CronCadence"/> carrying the
    /// expression.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Cron_BuildsCronCadence()
    {
        var builder = NewBuilder();
        builder.AddJob<TestWork>("cron-job")
            .Cron("*/5 * * * *")
            .DispatchTo<IWorkOrchestrator<TestWork>>(_ => new TestWork());

        var definition = SingleDefinition(builder);

        var cron = definition.Cadence as CronCadence;
        await Assert.That(cron).IsNotNull();
        await Assert.That(cron!.Expression).IsEqualTo("*/5 * * * *");
    }

    /// <summary>
    /// Verifies <c>.At(DateTimeOffset)</c> builds a <see cref="OneShotCadence"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task At_BuildsOneShotCadence()
    {
        var builder = NewBuilder();
        builder.AddJob<TestWork>("once-job")
            .At(At)
            .DispatchTo<IWorkOrchestrator<TestWork>>(_ => new TestWork());

        var definition = SingleDefinition(builder);

        var oneShot = definition.Cadence as OneShotCadence;
        await Assert.That(oneShot).IsNotNull();
        await Assert.That(oneShot!.FireAt).IsEqualTo(At);
    }

    /// <summary>
    /// Verifies <c>.After(TimeSpan)</c> builds a <see cref="RelativeOneShotCadence"/>.
    /// The registry resolves the relative delay to an absolute one-shot at
    /// registration time (Task 45); the builder only captures it.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task After_BuildsRelativeOneShotCadence()
    {
        var builder = NewBuilder();
        builder.AddJob<TestWork>("after-job")
            .After(TimeSpan.FromMinutes(10))
            .DispatchTo<IWorkOrchestrator<TestWork>>(_ => new TestWork());

        var definition = SingleDefinition(builder);

        var relative = definition.Cadence as RelativeOneShotCadence;
        await Assert.That(relative).IsNotNull();
        await Assert.That(relative!.Delay).IsEqualTo(TimeSpan.FromMinutes(10));
    }

    /// <summary>
    /// Verifies <c>.WithJitter(fraction)</c> updates the interval cadence's jitter.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WithJitter_UpdatesIntervalCadence()
    {
        var builder = NewBuilder();
        builder.AddJob<TestWork>("jitter-job")
            .Every(TimeSpan.FromMinutes(5))
            .WithJitter(0.25)
            .DispatchTo<IWorkOrchestrator<TestWork>>(_ => new TestWork());

        var definition = SingleDefinition(builder);

        var interval = definition.Cadence as IntervalCadence;
        await Assert.That(interval).IsNotNull();
        await Assert.That(interval!.Jitter).IsEqualTo(0.25);
        await Assert.That(interval.Interval).IsEqualTo(TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Verifies <c>.WithMissedFirePolicy(policy)</c> sets the definition's policy.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WithMissedFirePolicy_SetsPolicy()
    {
        var builder = NewBuilder();
        builder.AddJob<TestWork>("policy-job")
            .Every(TimeSpan.FromMinutes(5))
            .WithMissedFirePolicy(MissedFirePolicy.FireAllMissed)
            .DispatchTo<IWorkOrchestrator<TestWork>>(_ => new TestWork());

        var definition = SingleDefinition(builder);

        await Assert.That(definition.MissedFirePolicy).IsEqualTo(MissedFirePolicy.FireAllMissed);
    }

    /// <summary>
    /// Verifies the default missed-fire policy is <see cref="MissedFirePolicy.Coalesce"/>
    /// when none is set.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task MissedFirePolicy_DefaultsToCoalesce()
    {
        var builder = NewBuilder();
        builder.AddJob<TestWork>("default-policy")
            .Every(TimeSpan.FromMinutes(5))
            .DispatchTo<IWorkOrchestrator<TestWork>>(_ => new TestWork());

        var definition = SingleDefinition(builder);

        await Assert.That(definition.MissedFirePolicy).IsEqualTo(MissedFirePolicy.Coalesce);
    }

    /// <summary>
    /// Verifies <c>.DispatchTo&lt;TOrchestrator&gt;(fire)</c> sets the orchestrator
    /// dispatch kind and defaults the work class to <see cref="WorkClass.Batch"/>
    /// (post-CPQ #16-Q6: scheduled batch work never starves interactive).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchTo_DefaultsWorkClassToBatch()
    {
        var builder = NewBuilder();
        builder.AddJob<TestWork>("orch-job")
            .Every(TimeSpan.FromMinutes(5))
            .DispatchTo<IWorkOrchestrator<TestWork>>(_ => new TestWork());

        var definition = SingleDefinition(builder);

        await Assert.That(definition.DispatchKind).IsEqualTo("orchestrator");
        await Assert.That(definition.WorkClass).IsEqualTo(WorkClass.Batch);
    }

    /// <summary>
    /// Verifies an explicit work class on <c>.DispatchTo</c> overrides the
    /// <see cref="WorkClass.Batch"/> default.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchTo_WorkClassOverride_IsHonored()
    {
        var builder = NewBuilder();
        builder.AddJob<TestWork>("interactive-job")
            .Every(TimeSpan.FromMinutes(5))
            .DispatchTo<IWorkOrchestrator<TestWork>>(_ => new TestWork(), WorkClass.Interactive);

        var definition = SingleDefinition(builder);

        await Assert.That(definition.WorkClass).IsEqualTo(WorkClass.Interactive);
    }

    /// <summary>
    /// Verifies the orchestrator dispatcher factory builds an
    /// <see cref="OrchestratorJobDispatcher{TWork}"/> resolving the orchestrator and
    /// shared sink from the supplied service provider.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchTo_Factory_BuildsOrchestratorDispatcher()
    {
        var builder = NewBuilder();
        builder.AddJob<TestWork>("orch-job")
            .Every(TimeSpan.FromMinutes(5))
            .DispatchTo<IWorkOrchestrator<TestWork>>(_ => new TestWork());

        var definition = SingleDefinition(builder);
        var provider = BuildProviderWithOrchestratorAndSink();

        var dispatcher = definition.DispatcherFactory(provider);

        await Assert.That(dispatcher).IsTypeOf<OrchestratorJobDispatcher<TestWork>>();
    }

    /// <summary>
    /// Verifies <c>.Run(...)</c> on the inline builder sets the inline dispatch kind
    /// and builds an <see cref="InlineJobDispatcher"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Run_SetsInlineDispatch_BuildsInlineDispatcher()
    {
        var builder = NewBuilder();
        builder.AddInlineJob("inline-job")
            .Every(TimeSpan.FromMinutes(5))
            .Run((_, _) => ValueTask.CompletedTask);

        var definition = SingleDefinition(builder);

        await Assert.That(definition.DispatchKind).IsEqualTo("inline");

        var dispatcher = definition.DispatcherFactory(EmptyProvider());
        await Assert.That(dispatcher).IsTypeOf<InlineJobDispatcher>();
    }

    /// <summary>
    /// Verifies <c>.DispatchVia&lt;TDispatcher&gt;()</c> sets the custom dispatch
    /// kind and resolves the dispatcher from DI.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchVia_SetsCustomDispatch_ResolvesFromDi()
    {
        var builder = NewBuilder();
        builder.AddJob<TestWork>("custom-job")
            .Every(TimeSpan.FromMinutes(5))
            .DispatchVia<CustomDispatcher>();

        var definition = SingleDefinition(builder);

        await Assert.That(definition.DispatchKind).IsEqualTo("custom");

        var services = new ServiceCollection();
        services.AddSingleton<CustomDispatcher>();
        var provider = services.BuildServiceProvider();

        var dispatcher = definition.DispatcherFactory(provider);
        await Assert.That(dispatcher).IsTypeOf<CustomDispatcher>();
    }

    /// <summary>
    /// Verifies a fluent chain that mixes cadence, jitter, policy, and dispatch
    /// produces a single coherent definition.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FullChain_ProducesCoherentDefinition()
    {
        var builder = NewBuilder();
        builder.AddJob<TestWork>("full-job")
            .Every(TimeSpan.FromMinutes(2))
            .WithJitter(0.1)
            .WithMissedFirePolicy(MissedFirePolicy.SkipMissed)
            .DispatchTo<IWorkOrchestrator<TestWork>>(_ => new TestWork(), WorkClass.Default);

        var definition = SingleDefinition(builder);

        var interval = definition.Cadence as IntervalCadence;
        await Assert.That(interval).IsNotNull();
        await Assert.That(interval!.Interval).IsEqualTo(TimeSpan.FromMinutes(2));
        await Assert.That(interval.Jitter).IsEqualTo(0.1);
        await Assert.That(definition.MissedFirePolicy).IsEqualTo(MissedFirePolicy.SkipMissed);
        await Assert.That(definition.DispatchKind).IsEqualTo("orchestrator");
        await Assert.That(definition.WorkClass).IsEqualTo(WorkClass.Default);
    }

    private static SchedulerBuilder NewBuilder() => new(new ServiceCollection());

    private static JobDefinition SingleDefinition(SchedulerBuilder builder)
        => builder.Definitions.Single();

    private static IServiceProvider EmptyProvider()
        => new ServiceCollection().BuildServiceProvider();

    private static IServiceProvider BuildProviderWithOrchestratorAndSink()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkOrchestrator<TestWork>>(new FakeOrchestrator());
        services.AddSingleton<ISchedulerEventSink, RecordingSchedulerEventSink>();
        return services.BuildServiceProvider();
    }

    private sealed class TestWork;

    private sealed class CustomDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
            => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A minimal hand-written <see cref="IWorkOrchestrator{TWork}"/> fake. A private
    /// nested work type cannot be proxied by a mocking library, so the orchestrator
    /// dispatcher factory test resolves this concrete fake from DI instead.
    /// </summary>
    private sealed class FakeOrchestrator : IWorkOrchestrator<TestWork>
    {
        public int PendingCount => 0;

        public int ActiveWorkers => 0;

        public int Capacity => 0;

        public ValueTask<EnqueueResult> EnqueueAsync(
            TestWork work,
            WorkClass workClass = WorkClass.Default,
            CancellationToken ct = default)
            => ValueTask.FromResult(EnqueueResult.Accepted);

        public bool TryEnqueue(TestWork work, WorkClass workClass = WorkClass.Default) => true;

        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void Run(TestWork work, WorkClass workClass = WorkClass.Default)
        {
        }

        public bool TryRun(TestWork work, WorkClass workClass = WorkClass.Default) => true;

        public Func<string, CancellationToken, Task> CreateWorkerFunction()
            => static (_, _) => Task.CompletedTask;

        public Func<string, CancellationToken, Task> CreateWorkerFunction(Action<bool>? stateCallback)
            => static (_, _) => Task.CompletedTask;

        public Task RequestScaleUpAsync(int count, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RequestScaleDownAsync(int count, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DrainAsync(CancellationToken ct = default) => Task.CompletedTask;

        public CancellationToken GetShutdownToken() => CancellationToken.None;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
