// =============================================================================
// <copyright file="RegistryRegistrationBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;

namespace Bifrost.Benchmarks.Scheduling;

/// <summary>
/// Benchmarks for <see cref="ScheduleRegistry"/> lifecycle operations (DR-11, Task 41).
/// </summary>
/// <remarks>
/// <para>
/// Tracked targets (not gates):
/// <list type="bullet">
/// <item><see cref="Register_Single"/>: registration allocates under 256 B.</item>
/// <item><see cref="GetJobs_1000Jobs"/>: returns a single result-list allocation.</item>
/// </list>
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class RegistryRegistrationBenchmarks
{
    private const int BulkJobCount = 1000;

    // Fixed clock: benchmarks must not read the wall clock.
    private static readonly DateTimeOffset Epoch =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeProvider FrozenClock = new FixedTimeProvider(Epoch);

    // -----------------------------------------------------------------------
    // Register_Single — build + tear-down a fresh registry per invocation so
    // we measure registration cost, not amortised dict growth.
    // We do NOT use [IterationSetup] because we want GlobalSetup accuracy and
    // the iteration count to stay default. Instead the benchmark itself does
    // a full setup/teardown cycle so each call measures the same surface.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Measures the cost of registering a single job into a fresh
    /// <see cref="ScheduleRegistry"/>.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the benchmark operation.</returns>
    [Benchmark]
    public async ValueTask Register_Single()
    {
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, FrozenClock);
        await registry.RegisterAsync(
            "bench-single",
            Cadence.Interval(TimeSpan.FromMinutes(1)),
            MissedFirePolicy.SkipMissed,
            NoOpDispatcher.Instance).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Register_Bulk — registers BulkJobCount jobs into a single registry.
    // A fresh registry per iteration is achieved via GlobalSetup returning a
    // new one each call; since GlobalSetup runs once per parameter set, we
    // build the baseline inside the benchmark body to get accurate numbers.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Measures the cumulative cost of registering <see cref="BulkJobCount"/>
    /// jobs into a single <see cref="ScheduleRegistry"/>.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the benchmark operation.</returns>
    [Benchmark]
    public async ValueTask Register_Bulk()
    {
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, FrozenClock);
        for (var i = 0; i < BulkJobCount; i++)
        {
            await registry.RegisterAsync(
                $"bulk-{i:D5}",
                Cadence.Interval(TimeSpan.FromMinutes(1)),
                MissedFirePolicy.SkipMissed,
                NoOpDispatcher.Instance).ConfigureAwait(false);
        }
    }

    // -----------------------------------------------------------------------
    // Pause / Resume / Trigger — need a pre-populated registry.
    // GlobalSetup fills _warmRegistry with one job; each benchmark reads from
    // the same warm instance. Because we are measuring per-call overhead —
    // not the first-call cost — sharing is intentional and matches production.
    // -----------------------------------------------------------------------

    private ScheduleRegistry? _warmRegistry;

    /// <summary>
    /// Fills a shared registry with one named job for pause/resume/trigger benchmarks.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the global-setup operation.</returns>
    [GlobalSetup(Targets = [nameof(Pause_Single), nameof(Resume_Single), nameof(Trigger_Single), nameof(GetJobs_1000Jobs)])]
    public async Task GlobalSetup_WarmRegistry()
    {
        var store = new InMemoryScheduleStore();
        _warmRegistry = new ScheduleRegistry(store, FrozenClock);

        // Register the job used by pause/resume/trigger benchmarks.
        await _warmRegistry.RegisterAsync(
            "warm-job",
            Cadence.Interval(TimeSpan.FromMinutes(5)),
            MissedFirePolicy.SkipMissed,
            NoOpDispatcher.Instance).ConfigureAwait(false);

        // Pre-populate 1000 jobs for GetJobs_1000Jobs.
        for (var i = 0; i < BulkJobCount; i++)
        {
            await _warmRegistry.RegisterAsync(
                $"getjobs-{i:D5}",
                Cadence.Interval(TimeSpan.FromSeconds(30)),
                MissedFirePolicy.Coalesce,
                NoOpDispatcher.Instance).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Measures the cost of pausing a running job.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the benchmark operation.</returns>
    [Benchmark]
    public async ValueTask Pause_Single()
    {
        // Pause + immediately resume to keep the job in Running state across
        // consecutive benchmark iterations without a separate IterationSetup.
        await _warmRegistry!.PauseAsync("warm-job").ConfigureAwait(false);
        await _warmRegistry!.ResumeAsync("warm-job").ConfigureAwait(false);
    }

    /// <summary>
    /// Measures the cost of resuming a paused job.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the benchmark operation.</returns>
    [Benchmark]
    public async ValueTask Resume_Single()
    {
        // Pause first so we have a Paused job to resume, then restore state.
        await _warmRegistry!.PauseAsync("warm-job").ConfigureAwait(false);
        await _warmRegistry!.ResumeAsync("warm-job").ConfigureAwait(false);
    }

    /// <summary>
    /// Measures the cost of posting a trigger command for a registered job.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the benchmark operation.</returns>
    [Benchmark]
    public async ValueTask Trigger_Single()
    {
        await _warmRegistry!.TriggerAsync("warm-job").ConfigureAwait(false);
    }

    /// <summary>
    /// Measures <see cref="IScheduleRegistry.GetJobs"/> over a registry with
    /// <see cref="BulkJobCount"/> registered jobs. Tracked target: a single
    /// result-list allocation.
    /// </summary>
    /// <returns>The snapshot job list; returned to prevent dead-code elimination.</returns>
    [Benchmark]
    public IReadOnlyList<JobDescriptor> GetJobs_1000Jobs()
    {
        return _warmRegistry!.GetJobs();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// A <see cref="TimeProvider"/> that always returns a fixed instant.
    /// Avoids importing the testing package just for a static clock.
    /// </summary>
    private sealed class FixedTimeProvider(DateTimeOffset fixedTime) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => fixedTime;
    }

    /// <summary>
    /// A no-op <see cref="IJobDispatcher"/> that immediately completes without
    /// allocating, used as the dispatcher stub in all registry benchmarks.
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
}
