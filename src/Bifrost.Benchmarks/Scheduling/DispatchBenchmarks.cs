// =============================================================================
// <copyright file="DispatchBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;

using Bifrost.Core;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Dispatch;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Benchmarks.Scheduling;

/// <summary>
/// Benchmarks for the three dispatcher strategies (DR-4, DR-11, Task 44):
/// <see cref="OrchestratorJobDispatcher{TWork}"/>, <see cref="InlineJobDispatcher"/>,
/// and a custom <see cref="IJobDispatcher"/> stub.
/// </summary>
/// <remarks>
/// <para>
/// The measurement window is from the moment <see cref="IJobDispatcher.DispatchAsync"/>
/// is called (the point at which the router hands off the <see cref="JobFireContext"/>)
/// to when the dispatch target receives the work. Because all three dispatchers
/// ultimately do I/O-free work (no-op handler, no-op delegate, no-op stub),
/// the numbers capture dispatcher overhead rather than application time.
/// </para>
/// <para>
/// <see cref="OrchestratorDispatch_Overhead"/> uses a
/// <see cref="WorkOrchestrator{TWork}"/> with a no-op handler and a single worker,
/// matching the lightest realistic orchestrator setup. The enqueue is expected to
/// complete synchronously (unbounded channel, capacity well above 1), so the
/// allocation is the <c>ValueTask</c> path cost, not channel backpressure.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class DispatchBenchmarks
{
    private static readonly DateTimeOffset Epoch =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // A fixed fire context shared across all benchmark iterations; immutable struct,
    // no allocation risk from reuse.
    private static readonly JobFireContext FireContext = new(
        JobName: "bench-dispatch",
        FireTime: Epoch,
        NextFireAt: Epoch.AddMinutes(1),
        Services: NullServiceProvider.Instance);

    // -----------------------------------------------------------------------
    // OrchestratorDispatch_Overhead
    // -----------------------------------------------------------------------

    private WorkOrchestrator<JobFireContext>? _orchestrator;
    private OrchestratorJobDispatcher<JobFireContext>? _orchestratorDispatcher;

    /// <summary>
    /// Builds the <see cref="WorkOrchestrator{TWork}"/> used by
    /// <see cref="OrchestratorDispatch_Overhead"/>.
    /// </summary>
    [GlobalSetup(Target = nameof(OrchestratorDispatch_Overhead))]
    public void GlobalSetup_Orchestrator()
    {
        var options = Options.Create(new WorkOrchestratorOptions
        {
            Capacity = 65536,
            WorkerCount = 1,
        });
        _orchestrator = new WorkOrchestrator<JobFireContext>(
            new NoOpWorkHandler(),
            options,
            NullLogger<WorkOrchestrator<JobFireContext>>.Instance);

        _orchestratorDispatcher = new OrchestratorJobDispatcher<JobFireContext>(
            fireFunc: static ctx => ctx,
            orchestrator: _orchestrator,
            workClass: WorkClass.Batch,
            sink: NullSchedulerEventSink.Instance);
    }

    /// <summary>
    /// Releases the <see cref="WorkOrchestrator{TWork}"/> after
    /// <see cref="OrchestratorDispatch_Overhead"/> runs.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the cleanup operation.</returns>
    [GlobalCleanup(Target = nameof(OrchestratorDispatch_Overhead))]
    public async ValueTask GlobalCleanup_Orchestrator()
    {
        if (_orchestrator is not null)
        {
            await _orchestrator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Measures the end-to-end overhead of dispatching a fire through
    /// <see cref="OrchestratorJobDispatcher{TWork}"/>: building the work item
    /// from the fire context and enqueueing it on the orchestrator.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the benchmark operation.</returns>
    [Benchmark(Baseline = true, Description = "OrchestratorDispatch — enqueue to orchestrator")]
    public ValueTask OrchestratorDispatch_Overhead()
    {
        return _orchestratorDispatcher!.DispatchAsync(FireContext, CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // InlineDispatch_Overhead
    // -----------------------------------------------------------------------

    private InlineJobDispatcher? _inlineDispatcher;

    /// <summary>
    /// Builds the <see cref="InlineJobDispatcher"/> used by
    /// <see cref="InlineDispatch_Overhead"/>.
    /// </summary>
    [GlobalSetup(Target = nameof(InlineDispatch_Overhead))]
    public void GlobalSetup_Inline()
    {
        // The delegate completes immediately; the measurement captures the
        // Task.Run + await path cost inside InlineJobDispatcher.DispatchAsync.
        _inlineDispatcher = new InlineJobDispatcher(
            static (_, _) => ValueTask.CompletedTask);
    }

    /// <summary>
    /// Measures the overhead of <see cref="InlineJobDispatcher.DispatchAsync"/>:
    /// the Task.Run hand-off and await cost for a zero-work delegate.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the benchmark operation.</returns>
    [Benchmark(Description = "InlineDispatch — Task.Run + await delegate")]
    public ValueTask InlineDispatch_Overhead()
    {
        return _inlineDispatcher!.DispatchAsync(FireContext, CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // CustomDispatch_Overhead
    // -----------------------------------------------------------------------

    private IJobDispatcher? _customDispatcher;

    /// <summary>
    /// Builds the custom <see cref="IJobDispatcher"/> stub used by
    /// <see cref="CustomDispatch_Overhead"/>.
    /// </summary>
    [GlobalSetup(Target = nameof(CustomDispatch_Overhead))]
    public void GlobalSetup_Custom()
    {
        _customDispatcher = new NoOpCustomDispatcher();
    }

    /// <summary>
    /// Measures the minimum dispatch overhead for a custom
    /// <see cref="IJobDispatcher"/> that completes synchronously: the virtual
    /// dispatch cost and <see cref="ValueTask.CompletedTask"/> return.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the benchmark operation.</returns>
    [Benchmark(Description = "CustomDispatch — synchronous ValueTask.CompletedTask")]
    public ValueTask CustomDispatch_Overhead()
    {
        return _customDispatcher!.DispatchAsync(FireContext, CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// A no-op <see cref="IWorkHandler{TWork}"/> for the orchestrator benchmark.
    /// </summary>
    private sealed class NoOpWorkHandler : IWorkHandler<JobFireContext>
    {
        public ValueTask HandleAsync(JobFireContext work, CancellationToken ct)
            => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A synchronous custom dispatcher stub: the lower bound for any custom
    /// <see cref="IJobDispatcher"/> implementation.
    /// </summary>
    private sealed class NoOpCustomDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
            => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A no-op <see cref="ISchedulerEventSink"/> that discards every event.
    /// </summary>
    private sealed class NullSchedulerEventSink : ISchedulerEventSink
    {
        public static readonly NullSchedulerEventSink Instance = new();

        private NullSchedulerEventSink()
        {
        }

        public void Publish<TEvent>(in TEvent schedulerEvent)
            where TEvent : struct
        {
            // Intentionally discards the event.
        }
    }

    /// <summary>
    /// A no-op <see cref="IServiceProvider"/> that returns <see langword="null"/>
    /// for every service, used as the fire context's service scope.
    /// </summary>
    private sealed class NullServiceProvider : IServiceProvider
    {
        public static readonly NullServiceProvider Instance = new();

        private NullServiceProvider()
        {
        }

        public object? GetService(Type serviceType) => null;
    }
}
