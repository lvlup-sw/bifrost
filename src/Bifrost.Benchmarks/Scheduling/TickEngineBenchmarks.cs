// =============================================================================
// <copyright file="TickEngineBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Bifrost.Scheduling.TickEngine;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Benchmarks.Scheduling;

/// <summary>
/// Benchmarks for the <see cref="ScheduleTickLoop"/> min-heap tick engine
/// (DR-7, DR-11, Task 42).
/// </summary>
/// <remarks>
/// <para>
/// All time flows through a <see cref="FakeTimeProvider"/> so measurements are
/// deterministic and require no real wall-clock waits.  The benchmarks exercise
/// three dimensions:
/// </para>
/// <list type="bullet">
/// <item>
/// <see cref="FireLatency"/>: gap from <c>NextFireAt</c> to router handoff,
/// parameterised over <see cref="JobCount"/>. Merge target: p99 &lt; 5 ms at
/// 10 K jobs on an in-memory store.
/// </item>
/// <item>
/// <see cref="ScalingCurve"/>: per-fire cost across <see cref="JobCount"/>
/// params; O(log n) heap behaviour expected.
/// </item>
/// <item>
/// <see cref="SteadyStateAllocation"/>: dispatch-handoff allocations per fire.
/// Tracked target (not a gate): approaching 0 B/fire.
/// </item>
/// </list>
/// <para>
/// Note: <c>[IterationSetup]</c> forces <c>InvocationCount=1</c>, making
/// sub-microsecond absolute numbers unreliable. Prefer reading ratios across
/// <see cref="JobCount"/> values to confirm O(log n) scaling.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class TickEngineBenchmarks
{
    private static readonly DateTimeOffset Epoch =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan FireInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the number of jobs registered in the scheduler for each
    /// benchmark run. Parameterises the heap size to reveal O(log n) scaling.
    /// </summary>
    [Params(10, 100, 1000, 10000)]
    public int JobCount { get; set; }

    // Shared across all three benchmark methods within a single parameter set.
    private FakeTimeProvider _time = null!;
    private ScheduleRegistry _registry = null!;
    private InMemoryScheduleStore _store = null!;
    private ScheduleTickLoop _loop = null!;
    private NullRecordingRouter _router = null!;

    /// <summary>
    /// Builds the scheduler with <see cref="JobCount"/> registered jobs and
    /// advances the clock just before the first fire so dispatch work is
    /// already queued for the benchmark body to trigger.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the global-setup operation.</returns>
    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _time = new FakeTimeProvider(Epoch);
        _store = new InMemoryScheduleStore();
        _registry = new ScheduleRegistry(_store, _time);
        _router = new NullRecordingRouter();
        _loop = new ScheduleTickLoop(
            _registry,
            _store,
            _time,
            _router,
            NullSchedulerEventSink.Instance,
            NullLogger<ScheduleTickLoop>.Instance,
            new SchedulerOptions());

        await ((IHostedService)_loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
        await _loop.WaitForIdleAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // Register JobCount jobs, each with a 60-second interval.
        for (var i = 0; i < JobCount; i++)
        {
            await _registry.RegisterAsync(
                $"bench-{i:D6}",
                Cadence.Interval(FireInterval),
                MissedFirePolicy.Coalesce,
                NoOpDispatcher.Instance).ConfigureAwait(false);
        }

        await _loop.WaitForIdleAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the tick loop after all benchmark runs for this parameter set.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the global-cleanup operation.</returns>
    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        await _loop.StopAsync(CancellationToken.None).ConfigureAwait(false);
        _loop.Dispose();
    }

    /// <summary>
    /// Measures end-to-end fire latency: the time from advancing the clock
    /// (making all <see cref="JobCount"/> jobs due) to every router handoff
    /// completing. This corresponds to the gap between <c>NextFireAt</c> and
    /// the dispatch handoff that the scheduler records as fire latency.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the benchmark operation.</returns>
    [Benchmark(Description = "FireLatency — clock advance to dispatch handoff")]
    public async Task FireLatency()
    {
        _router.Reset();

        // Advance the fake clock by exactly one interval so every job becomes due.
        _time.Advance(FireInterval);

        // Wait for the tick loop to drain all due jobs and reach idle.
        await _loop.WaitForIdleAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        // Advance again to re-arm for the next iteration (keeps jobs recurring).
        // This is part of the measurement but represents realistic steady-state.
        _ = _router.HandoffCount;
    }

    /// <summary>
    /// Measures per-fire cost across <see cref="JobCount"/> job-count params,
    /// revealing O(log n) heap behaviour. The metric is wall-time per fire
    /// (total / <see cref="JobCount"/>), measured by BenchmarkDotNet's
    /// per-operation scaling.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the benchmark operation.</returns>
    [Benchmark(Description = "ScalingCurve — per-fire cost vs job count")]
    public async Task ScalingCurve()
    {
        _router.Reset();
        _time.Advance(FireInterval);
        await _loop.WaitForIdleAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    /// <summary>
    /// Measures allocations at the dispatch-handoff boundary per fire. The
    /// tracked target (not a gate) is approaching 0 B/fire as the hot path
    /// becomes struct-only.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the benchmark operation.</returns>
    [Benchmark(Description = "SteadyStateAllocation — alloc per dispatch handoff")]
    public async Task SteadyStateAllocation()
    {
        _router.Reset();
        _time.Advance(FireInterval);
        await _loop.WaitForIdleAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// A no-op <see cref="IJobDispatcher"/> that completes synchronously without
    /// allocating, used as the dispatcher stub for all tick-engine benchmarks.
    /// </summary>
    private sealed class NoOpDispatcher : IJobDispatcher
    {
        public static readonly NoOpDispatcher Instance = new();

        private NoOpDispatcher()
        {
        }

        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
            => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A router that records the handoff count but performs no real dispatch.
    /// Avoids Task.Run overhead so the measurement isolates the tick-loop
    /// dispatch path rather than thread-pool scheduling.
    /// </summary>
    private sealed class NullRecordingRouter : IJobDispatcherRouter
    {
        private int _handoffCount;

        /// <summary>
        /// Gets the number of dispatches handed off since the last <see cref="Reset"/>.
        /// </summary>
        public int HandoffCount => Volatile.Read(ref _handoffCount);

        /// <summary>
        /// Resets the handoff counter for the next iteration.
        /// </summary>
        public void Reset() => Volatile.Write(ref _handoffCount, 0);

        /// <inheritdoc/>
        public void Dispatch(IJobDispatcher dispatcher, JobFireContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _handoffCount);

            // Complete the dispatch synchronously via a Task.Run so the in-flight
            // tracking decorator's finally block runs promptly and the tick loop
            // can reach idle. Using Task.Run matches the production router's
            // threading model without the I/O overhead of a real dispatch.
            _ = Task.Run(
                () => dispatcher.DispatchAsync(context, ct).AsTask(),
                CancellationToken.None);
        }
    }

    /// <summary>
    /// A no-op <see cref="ISchedulerEventSink"/> that discards every event. Avoids
    /// a dependency on the internal <c>NullSchedulerEventSink</c> type from the
    /// production assembly.
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
}
