// =============================================================================
// <copyright file="ScheduleTickLoop.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Registry;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bifrost.Scheduling.TickEngine;

/// <summary>
/// The min-heap tick engine that drives the scheduler (DR-7). A
/// <see cref="BackgroundService"/> that owns a priority queue keyed by each job's
/// next-fire instant: it sleeps until the nearest fire is due or a registry command
/// wakes it, drains the command, dispatches every due job through the router, and
/// re-enqueues recurring jobs at their next occurrence.
/// </summary>
/// <remarks>
/// <para>
/// All time flows through the injected <see cref="TimeProvider"/> (DR-7): the loop
/// sleeps with <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
/// and never reads a wall clock. The scheduled occurrence time — the heap key, not
/// a wall-clock read — is the fire instant handed to the dispatcher and recorded as
/// <c>LastFiredAt</c>, so a fire is stable and idempotent across re-evaluation.
/// </para>
/// <para>
/// The loop never awaits user code on its own thread: every fire is handed to the
/// <see cref="IJobDispatcherRouter"/>, which runs it on the pool and isolates a
/// throwing dispatcher as a
/// <see cref="Bifrost.Scheduling.Core.Events.JobFireFailedEvent"/>.
/// </para>
/// </remarks>
public sealed partial class ScheduleTickLoop : BackgroundService, IDisposable
{
    private readonly ScheduleRegistry registry;
    private readonly IScheduleStore store;
    private readonly TimeProvider timeProvider;
    private readonly IJobDispatcherRouter router;
    private readonly ISchedulerEventSink eventSink;
    private readonly ILogger<ScheduleTickLoop> logger;
    private readonly SchedulerOptions options;

    // The min-heap of pending fires keyed by scheduled occurrence. Owned by — and
    // only ever touched on — the single tick thread, so it needs no lock.
    private readonly PriorityQueue<JobHandle, DateTimeOffset> heap = new();

    // The live per-job scheduling state, keyed by job name. The authoritative
    // record of which jobs are currently armed and at which generation; the heap may
    // hold stale entries that this map invalidates (lazy deletion).
    private readonly Dictionary<string, ScheduledJob> scheduled = new(StringComparer.Ordinal);

    // Quiescence barrier: WaitForIdleAsync posts a TaskCompletionSource here, which
    // also wakes the loop. Each time the loop reaches a fully-quiescent park — all
    // commands drained, all due jobs dispatched, no dispatch in flight — it completes
    // every pending barrier request. A request posted before that park is therefore
    // guaranteed to be completed by it, with no dependence on channel counting. Group
    // J's public test harness wraps WaitForIdleAsync.
    private readonly Channel<TaskCompletionSource> idleBarrier =
        Channel.CreateUnbounded<TaskCompletionSource>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    // Wakes the wait when the last in-flight dispatch completes, so the loop can
    // re-evaluate its idle state once a pool-thread fire it handed off has finished.
    private readonly object drainGate = new();
    private TaskCompletionSource dispatchesDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long generationCounter;
    private int inFlightDispatches;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScheduleTickLoop"/> class.
    /// </summary>
    /// <param name="registry">
    /// The live registry: source of the wake-command stream and of each job's
    /// current record and dispatcher.
    /// </param>
    /// <param name="store">The durable store, seeded at startup and checkpointed on fire.</param>
    /// <param name="timeProvider">The clock the loop sleeps and computes against (DR-7).</param>
    /// <param name="router">The dispatch executor every fire is handed to.</param>
    /// <param name="eventSink">The seam fire and fault events are published through.</param>
    /// <param name="logger">The loop's logger.</param>
    /// <param name="options">The scheduler options.</param>
    internal ScheduleTickLoop(
        ScheduleRegistry registry,
        IScheduleStore store,
        TimeProvider timeProvider,
        IJobDispatcherRouter router,
        ISchedulerEventSink eventSink,
        ILogger<ScheduleTickLoop> logger,
        SchedulerOptions options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(eventSink);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        this.registry = registry;
        this.store = store;
        this.timeProvider = timeProvider;
        this.router = router;
        this.eventSink = eventSink;
        this.logger = logger;
        this.options = options;
    }

    /// <summary>
    /// Awaits the loop's next quiescent point: completes once the loop has drained
    /// all pending commands, dispatched every due job, and is about to wait with no
    /// in-flight dispatches. Tests use this — together with a
    /// <see cref="TimeProvider"/> advance — to deterministically observe the loop's
    /// settled state without any real sleep. Group J's public
    /// <c>ISchedulerTestHarness</c> wraps this primitive.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the loop to go idle.</param>
    /// <returns>A task that completes when the loop next reaches its idle point.</returns>
    [SuppressMessage(
        "Usage",
        "VSTHRD003:Avoid awaiting foreign Tasks",
        Justification = "The barrier completion is an intentional cross-thread " +
            "quiescence primitive completed by the tick thread at its next quiescent " +
            "park; awaiting it here is the point.")]
    internal async Task WaitForIdleAsync(TimeSpan timeout)
    {
        // Post a barrier request and wake the loop. Because the loop drains every
        // command and dispatches every due job before it parks, and completes all
        // pending barrier requests at that quiescent park, a request posted now is
        // guaranteed to be completed at the loop's next quiescent point — after it has
        // absorbed whatever command or clock advance preceded this call.
        var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await this.idleBarrier.Writer.WriteAsync(request).ConfigureAwait(false);

        var deadline = Task.Delay(timeout);
        var completed = await Task.WhenAny(request.Task, deadline).ConfigureAwait(false);
        if (completed == deadline)
        {
            throw new TimeoutException(
                $"The tick loop did not reach an idle point within {timeout}.");
        }
    }

    /// <summary>
    /// Completes every pending quiescence-barrier request. Called by the tick thread
    /// only at a fully-quiescent park — all commands drained, all due jobs dispatched,
    /// no dispatch in flight — so a completed request is a true idle signal.
    /// </summary>
    private void ReleaseIdleBarrier()
    {
        while (this.idleBarrier.Reader.TryRead(out var request))
        {
            request.TrySetResult();
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await this.SeedAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            await this.RunTickAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a single tick: drains commands, dispatches every due job, then waits for
    /// the nearest fire or a wake source. Draining and dispatching happen first so the
    /// loop absorbs all pending work before it parks and releases the idle barrier.
    /// </summary>
    /// <param name="stoppingToken">The loop's stopping token.</param>
    /// <returns>A task that completes when the tick has been processed.</returns>
    private async Task RunTickAsync(CancellationToken stoppingToken)
    {
        this.DrainCommands();
        this.DispatchDueJobs(stoppingToken);
        await this.WaitForNextWakeAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sleeps until the nearest scheduled fire comes due, a registry wake command
    /// arrives, an in-flight dispatch drains, or a quiescence-barrier request is
    /// posted — whichever is first. When the heap is empty it waits without a timer,
    /// so an empty registry idles without busy-waiting. At a fully-quiescent park —
    /// nothing due and no dispatch in flight — it releases the idle barrier so a
    /// waiting test observes the settled state.
    /// </summary>
    /// <param name="stoppingToken">The loop's stopping token.</param>
    /// <returns>A task that completes when the loop should re-evaluate.</returns>
    [SuppressMessage(
        "Usage",
        "VSTHRD003:Avoid awaiting foreign Tasks",
        Justification = "The drain signal is an intentional cross-thread primitive " +
            "completed when the last pool-thread dispatch finishes; awaiting it wakes " +
            "the loop to re-evaluate idle.")]
    private async Task WaitForNextWakeAsync(CancellationToken stoppingToken)
    {
        var now = this.timeProvider.GetUtcNow();
        var nextFire = this.PeekNextValidFire();

        // A due job (or a past-due one) means do not wait — loop straight back to
        // dispatch it.
        if (nextFire is not null && nextFire.Value <= now)
        {
            return;
        }

        var inFlight = Volatile.Read(ref this.inFlightDispatches) > 0;

        // Fully quiescent: nothing due and no dispatch in flight. Release the idle
        // barrier so any waiting test observes the settled state. (When a dispatch is
        // in flight the loop is not yet idle; the barrier is released at the next park
        // after the dispatch drains.)
        if (!inFlight)
        {
            this.ReleaseIdleBarrier();
        }

        // Arm every wait source so the loop wakes on the earliest of: a registry
        // command, the next fire's timer, a dispatch draining, or a barrier request.
        // The Task.Delay is created before awaiting so that a test which advances the
        // FakeTimeProvider after observing idle reliably fires it (no missed wakeup).
        var drainTask = this.CurrentDrainTask();
        var commandReady = this.registry.Commands.WaitToReadAsync(stoppingToken).AsTask();
        var barrierReady = this.idleBarrier.Reader.WaitToReadAsync(stoppingToken).AsTask();

        try
        {
            if (nextFire is null)
            {
                // Empty heap: wait on the command stream, dispatch drain, and barrier.
                await Task.WhenAny(commandReady, drainTask, barrierReady).ConfigureAwait(false);
                return;
            }

            var delay = nextFire.Value - now;
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            var delayTask = Task.Delay(delay, this.timeProvider, stoppingToken);
            await Task.WhenAny(commandReady, delayTask, drainTask, barrierReady).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
    }

    /// <summary>
    /// Drains every pending registry command and applies it to the heap.
    /// </summary>
    private void DrainCommands()
    {
        while (this.registry.Commands.TryRead(out var command))
        {
            this.ApplyCommand(command);
        }
    }

    /// <summary>
    /// Applies a single registry command to the scheduling state.
    /// </summary>
    /// <param name="command">The command to apply.</param>
    private void ApplyCommand(RegistryCommand command)
    {
        switch (command.Kind)
        {
            case RegistryCommandKind.Register:
            case RegistryCommandKind.Resume:
                this.Arm(command.JobName);
                break;

            case RegistryCommandKind.Unregister:
            case RegistryCommandKind.Pause:
                this.Disarm(command.JobName);
                break;

            case RegistryCommandKind.Trigger:
                this.FireImmediately(command.JobName);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Arms (or re-arms) a job from its current registry record: reads the record,
    /// computes its next fire if needed, and enqueues it. A job that is not running or
    /// has no next fire is left disarmed.
    /// </summary>
    /// <param name="jobName">The job to arm.</param>
    private void Arm(string jobName)
    {
        var record = this.registry.TryGetRecord(jobName);
        if (record is null || record.State != JobState.Running)
        {
            // Job removed or not currently running: ensure it is not armed.
            this.scheduled.Remove(jobName);
            return;
        }

        var now = this.timeProvider.GetUtcNow();
        var nextFire = record.NextFireAt
            ?? record.Cadence.ComputeNextFire(record.LastFiredAt, now);

        if (nextFire is null)
        {
            // No further occurrence (for example an already-fired one-shot).
            this.scheduled.Remove(jobName);
            return;
        }

        this.Enqueue(jobName, record.Cadence, record.MissedFirePolicy, nextFire.Value);
    }

    /// <summary>
    /// Disarms a job: drops its live scheduling state so any heap entries for it are
    /// treated as stale and discarded on pop.
    /// </summary>
    /// <param name="jobName">The job to disarm.</param>
    private void Disarm(string jobName) => this.scheduled.Remove(jobName);

    /// <summary>
    /// Fires a job out of band in response to a trigger, regardless of its schedule.
    /// The job's normal cadence is left untouched: the trigger is an extra fire, not a
    /// reschedule.
    /// </summary>
    /// <param name="jobName">The job to fire immediately.</param>
    private void FireImmediately(string jobName)
    {
        var record = this.registry.TryGetRecord(jobName);
        if (record is null)
        {
            return;
        }

        var now = this.timeProvider.GetUtcNow();
        var nextNext = record.NextFireAt
            ?? record.Cadence.ComputeNextFire(record.LastFiredAt, now);

        this.Dispatch(jobName, now, nextNext);
    }

    /// <summary>
    /// Dispatches every job whose scheduled occurrence is at or before the current
    /// instant, re-enqueuing recurring jobs at their next occurrence and dropping
    /// exhausted ones. Stale heap entries (invalidated by a pause, removal, or
    /// reschedule) are discarded.
    /// </summary>
    /// <param name="stoppingToken">The loop's stopping token.</param>
    private void DispatchDueJobs(CancellationToken stoppingToken)
    {
        var now = this.timeProvider.GetUtcNow();

        while (this.heap.TryPeek(out _, out var occurrence) && occurrence <= now)
        {
            this.heap.TryDequeue(out var handle, out _);

            // Lazy deletion: skip an entry whose job is no longer armed at this
            // generation (paused, removed, or already rescheduled).
            if (handle is null || !this.IsCurrent(handle))
            {
                continue;
            }

            var scheduledOccurrence = handle.ScheduledOccurrence;

            // Compute the next occurrence strictly after this one by anchoring the
            // clock at the occurrence itself, not the wall clock. This makes a single
            // large clock advance that spans several occurrences fire each of them in
            // turn (the re-enqueued occurrence is re-examined by this same loop), and
            // sets up the strict-occurrence next-fire computation a later group relies
            // on rather than skipping the backlog as a now-anchored interval would.
            var nextNext = handle.Cadence.ComputeNextFire(scheduledOccurrence, scheduledOccurrence);

            this.Dispatch(handle.JobName, scheduledOccurrence, nextNext);

            if (nextNext is null)
            {
                // One-shot (or exhausted cadence): drop it from the live state.
                this.scheduled.Remove(handle.JobName);
            }
            else
            {
                this.Enqueue(handle.JobName, handle.Cadence, handle.MissedFirePolicy, nextNext.Value);
            }
        }
    }

    /// <summary>
    /// Performs a single fire: resolves the live dispatcher, hands the fire to the
    /// router (fire-and-forget), publishes <see cref="JobFiredEvent"/>, and
    /// checkpoints the fire to the store best-effort. The fire instant is the
    /// scheduled occurrence — never a wall-clock read — so it is stable across
    /// re-evaluation and serves as the idempotency key.
    /// </summary>
    /// <param name="jobName">The job firing.</param>
    /// <param name="scheduledOccurrence">The occurrence's logical instant.</param>
    /// <param name="nextFireAt">The next occurrence, or <see langword="null"/>.</param>
    private void Dispatch(string jobName, DateTimeOffset scheduledOccurrence, DateTimeOffset? nextFireAt)
    {
        var dispatcher = this.registry.TryGetDispatcher(jobName);
        if (dispatcher is null)
        {
            // No live dispatcher (job removed mid-flight): surface as a failed fire
            // rather than silently dropping it.
            this.eventSink.Publish(new JobFireFailedEvent(
                jobName,
                scheduledOccurrence,
                Exception: null,
                Reason: "No dispatcher is registered for the job."));
            return;
        }

        var context = new JobFireContext(
            jobName,
            scheduledOccurrence,
            nextFireAt,
            EmptyServiceProvider.Instance);

        // Track the dispatch as in-flight before handing it to the router, and clear
        // the count when the wrapped dispatcher completes (success or throw). The
        // router runs the dispatch on the pool; the decorator's finally still runs
        // even when the router catches a throwing dispatcher, so the count is exact.
        Interlocked.Increment(ref this.inFlightDispatches);
        var tracked = new InFlightTrackingDispatcher(this, dispatcher);
        this.router.Dispatch(tracked, context, CancellationToken.None);

        this.eventSink.Publish(new JobFiredEvent(jobName, scheduledOccurrence, nextFireAt));

        // Best-effort checkpoint: the fire already happened, so a checkpoint failure
        // must not undo it or stall the loop.
        this.Checkpoint(jobName, scheduledOccurrence, nextFireAt);
    }

    /// <summary>
    /// Checkpoints a fire to the store, fire-and-forget. The fire has already been
    /// dispatched, so this is best-effort: a store fault is swallowed here and the
    /// loop continues.
    /// </summary>
    /// <param name="jobName">The job that fired.</param>
    /// <param name="firedAt">The fire instant.</param>
    /// <param name="nextFireAt">The next occurrence, or <see langword="null"/>.</param>
    private void Checkpoint(string jobName, DateTimeOffset firedAt, DateTimeOffset? nextFireAt)
        => _ = this.store.RecordFiredAsync(jobName, firedAt, nextFireAt, CancellationToken.None);

    /// <summary>
    /// Enqueues (or re-enqueues) a job at a scheduled occurrence under a fresh
    /// generation, so any prior heap entry for the job becomes stale.
    /// </summary>
    /// <param name="jobName">The job to enqueue.</param>
    /// <param name="cadence">The job's cadence.</param>
    /// <param name="policy">The job's missed-fire policy.</param>
    /// <param name="occurrence">The scheduled occurrence (the heap key).</param>
    private void Enqueue(string jobName, Cadence cadence, MissedFirePolicy policy, DateTimeOffset occurrence)
    {
        var generation = ++this.generationCounter;
        var handle = new JobHandle(jobName, cadence, policy, occurrence, generation);
        this.scheduled[jobName] = new ScheduledJob(generation, occurrence);
        this.heap.Enqueue(handle, occurrence);
    }

    /// <summary>
    /// Returns whether a popped handle still represents its job's live, current
    /// scheduling state — the job is armed and its generation matches.
    /// </summary>
    /// <param name="handle">The popped handle to validate.</param>
    /// <returns><see langword="true"/> when the handle is current.</returns>
    private bool IsCurrent(JobHandle handle)
        => this.scheduled.TryGetValue(handle.JobName, out var live)
            && live.Generation == handle.Generation;

    /// <summary>
    /// Peeks the nearest valid (non-stale) fire instant in the heap, discarding stale
    /// entries it encounters at the front. Returns <see langword="null"/> when no
    /// armed job remains.
    /// </summary>
    /// <returns>The nearest valid fire instant, or <see langword="null"/>.</returns>
    private DateTimeOffset? PeekNextValidFire()
    {
        while (this.heap.TryPeek(out var handle, out var occurrence))
        {
            if (this.IsCurrent(handle))
            {
                return occurrence;
            }

            // Drop the stale entry and look at the next.
            this.heap.Dequeue();
        }

        return null;
    }

    /// <summary>
    /// Loads persisted jobs and arms each currently registered, running one. The
    /// store supplies timing state (cadence, last/next fire, policy); the registry
    /// supplies the live dispatcher. A job present in the store but absent from the
    /// live registry has no dispatcher and is not armed.
    /// </summary>
    /// <param name="stoppingToken">The loop's stopping token.</param>
    /// <returns>A task that completes when seeding is done.</returns>
    private async Task SeedAsync(CancellationToken stoppingToken)
    {
        // Drain any commands posted before the loop started (registrations made
        // during host construction) so the heap reflects them.
        this.DrainCommands();

        _ = await this.store.LoadAllAsync(stoppingToken).ConfigureAwait(false);

        // Arm every currently registered running job from its live record.
        foreach (var record in this.registry.SnapshotRecords())
        {
            if (record.State == JobState.Running)
            {
                this.Arm(record.Name);
            }
        }
    }

    /// <summary>
    /// Returns the current dispatch-drain task: it completes when the last in-flight
    /// dispatch finishes, waking the tick loop to re-evaluate its idle state.
    /// </summary>
    /// <returns>The current drain task.</returns>
    [SuppressMessage(
        "Usage",
        "VSTHRD003:Avoid awaiting foreign Tasks",
        Justification = "Returns the intentional cross-thread drain signal for the " +
            "tick loop's wait to observe; it is completed by the pool-thread dispatch.")]
    private Task CurrentDrainTask()
    {
        lock (this.drainGate)
        {
            return this.dispatchesDrained.Task;
        }
    }

    /// <summary>
    /// Called from the wrapped dispatcher when a single fire completes (success or
    /// throw). Decrements the in-flight count and, when it reaches zero, completes the
    /// current drain task and arms a fresh one so the tick loop re-checks idle.
    /// </summary>
    private void OnDispatchCompleted()
    {
        if (Interlocked.Decrement(ref this.inFlightDispatches) != 0)
        {
            return;
        }

        lock (this.drainGate)
        {
            var previous = this.dispatchesDrained;
            this.dispatchesDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }

    /// <summary>
    /// A no-op <see cref="IServiceProvider"/> used as the fire context's service
    /// scope until a per-fire scope is wired in a later group. Returns
    /// <see langword="null"/> for every service.
    /// </summary>
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();

        public object? GetService(Type serviceType) => null;
    }

    /// <summary>
    /// Wraps the job's live dispatcher so the loop can count the dispatch as
    /// in-flight for the duration of the fire. The <c>finally</c> runs even when the
    /// inner dispatcher throws — the router catches the throw, but the await unwinds
    /// through this method first — so the in-flight count is always cleared exactly
    /// once per fire.
    /// </summary>
    /// <param name="owner">The loop tracking the in-flight count.</param>
    /// <param name="inner">The job's live dispatcher.</param>
    private sealed class InFlightTrackingDispatcher(ScheduleTickLoop owner, IJobDispatcher inner) : IJobDispatcher
    {
        public async ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            try
            {
                await inner.DispatchAsync(context, ct).ConfigureAwait(false);
            }
            finally
            {
                owner.OnDispatchCompleted();
            }
        }
    }

    /// <summary>
    /// The live scheduling state of a single armed job: the generation under which it
    /// was last enqueued and the occurrence it is armed for. Used for lazy deletion —
    /// a heap entry whose generation no longer matches is stale.
    /// </summary>
    /// <param name="Generation">The generation the job was last enqueued under.</param>
    /// <param name="Occurrence">The occurrence the job is currently armed for.</param>
    private readonly record struct ScheduledJob(long Generation, DateTimeOffset Occurrence);
}
