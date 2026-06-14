// =============================================================================
// <copyright file="ScheduleTickLoop.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Internal;
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
    /// <summary>
    /// The maximum number of missed occurrences reconciled at startup — mirrors the
    /// default cap of <see cref="MissedFirePolicyApplier.ComputeMissedFires"/>, used to
    /// detect and log a capped <see cref="MissedFirePolicy.FireAllMissed"/> backlog.
    /// </summary>
    private const int MissedFirePolicyApplierCatchUpCap = 100;

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

    // Quiescence barrier. WaitForIdleAsync adds a TaskCompletionSource to the pending
    // list and completes the wake signal so the parked loop re-evaluates. Each time the
    // loop reaches a fully-quiescent park — all commands drained, all due jobs
    // dispatched, no dispatch in flight — it completes and clears every pending
    // request. A request added before that park is therefore guaranteed to be completed
    // by it, with no dependence on channel counting or reader semantics. Group J's
    // public test harness wraps WaitForIdleAsync.
    private readonly object barrierGate = new();
    private readonly List<TaskCompletionSource> pendingBarriers = [];
    private TaskCompletionSource barrierWake =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Wakes the wait when the last in-flight dispatch completes, so the loop can
    // re-evaluate its idle state once a pool-thread fire it handed off has finished.
    private readonly object drainGate = new();
    private TaskCompletionSource dispatchesDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Restart timestamps within the sliding fault-recovery window (DR-10). Touched
    // only on the tick thread inside HandleTickLoopFault, so it needs no lock.
    private readonly List<DateTimeOffset> restartTimes = [];

    // Faulted-state barrier: WaitForFaultedAsync registers a TCS that the loop
    // completes when it transitions to faulted, mirroring the idle barrier.
    private readonly object faultedGate = new();
    private readonly List<TaskCompletionSource> pendingFaulted = [];

    private long generationCounter;
    private int inFlightDispatches;
    private int stopState;
    private int faultedState;

    // The previous monotonic clock reading, in UTC ticks (long.MinValue = unset), used
    // to detect a non-monotonic clock (DR-10).
    private long previousNowTicks = long.MinValue;

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
        // Register a barrier request and wake the loop. Because the loop drains every
        // command and dispatches every due job before it parks, and completes all
        // pending barrier requests at that quiescent park, a request registered now is
        // guaranteed to be completed at the loop's next quiescent point — after it has
        // absorbed whatever command or clock advance preceded this call.
        var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (this.barrierGate)
        {
            this.pendingBarriers.Add(request);

            // Wake a parked loop so it re-evaluates and reaches its quiescent park.
            var previousWake = this.barrierWake;
            this.barrierWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            previousWake.TrySetResult();
        }

        var deadline = Task.Delay(timeout);
        var completed = await Task.WhenAny(request.Task, deadline).ConfigureAwait(false);
        if (completed == deadline)
        {
            throw new TimeoutException(
                $"The tick loop did not reach an idle point within {timeout}.");
        }
    }

    /// <summary>
    /// Returns the current barrier wake task, completed when a new barrier request is
    /// registered so the parked loop re-evaluates its quiescence.
    /// </summary>
    /// <returns>The current barrier wake task.</returns>
    [SuppressMessage(
        "Usage",
        "VSTHRD003:Avoid awaiting foreign Tasks",
        Justification = "Returns the intentional cross-thread barrier wake signal for " +
            "the tick loop's wait to observe; it is completed when WaitForIdleAsync " +
            "registers a request.")]
    private Task CurrentBarrierWakeTask()
    {
        lock (this.barrierGate)
        {
            return this.barrierWake.Task;
        }
    }

    /// <summary>
    /// Completes and clears every pending quiescence-barrier request. Called by the
    /// tick thread only at a fully-quiescent park — all commands drained, all due jobs
    /// dispatched, no dispatch in flight — so a completed request is a true idle signal.
    /// </summary>
    private void ReleaseIdleBarrier()
    {
        lock (this.barrierGate)
        {
            foreach (var request in this.pendingBarriers)
            {
                request.TrySetResult();
            }

            this.pendingBarriers.Clear();
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // DR-6: the loop is unconditionally always-leader — there is no leadership,
        // lease, or coordination path. Opting into multi-instance against the default
        // non-exclusive store therefore produces duplicate fires; warn prominently.
        if (this.options.MultiInstanceExpected)
        {
            this.LogMultiInstanceWarning();
        }

        await this.SeedAsync(stoppingToken).ConfigureAwait(false);

        // Fault-recovery loop (DR-10). A fault in the loop's own code — not an isolated
        // dispatch, which the router catches — is logged critical, surfaced as a
        // SchedulerFaultedEvent, and the tick loop restarts. Too many restarts in the
        // window indicates a crash loop, not a transient fault, so the scheduler
        // transitions to its faulted state and stops ticking.
        while (!stoppingToken.IsCancellationRequested && !this.IsFaulted)
        {
            try
            {
                await this.RunTickLoopAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown.
                break;
            }
#pragma warning disable CA1031 // The recovery policy must catch every loop fault.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                this.HandleTickLoopFault(ex);
            }
        }

        // Unblock any test waiting on idle once the loop exits (faulted or stopped).
        this.ReleaseIdleBarrier();
    }

    /// <summary>
    /// Gets a value indicating whether the scheduler has transitioned to its faulted
    /// state — its tick loop crashed too many times in the restart window and has
    /// stopped ticking (DR-10). A later group's health check reads this.
    /// </summary>
    internal bool IsFaulted => Volatile.Read(ref this.faultedState) == 1;

    /// <summary>
    /// Runs the tick loop until cancelled, re-arming the loop's notion of "now" each
    /// iteration so a non-monotonic clock is handled (DR-10).
    /// </summary>
    /// <param name="stoppingToken">The loop's stopping token.</param>
    /// <returns>A task that completes when the loop is cancelled.</returns>
    private async Task RunTickLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await this.RunTickAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handles a fault in the loop's own code: logs critical, publishes a
    /// <see cref="SchedulerFaultedEvent"/>, and records the restart in the sliding
    /// window. When the restart count exceeds <see cref="SchedulerOptions.MaxRestartsInWindow"/>
    /// within <see cref="SchedulerOptions.RestartWindow"/>, the scheduler transitions to
    /// its faulted state and stops ticking.
    /// </summary>
    /// <param name="ex">The fault that crashed the loop body.</param>
    private void HandleTickLoopFault(Exception ex)
    {
        this.LogTickLoopFaulted(ex);

        var now = this.timeProvider.GetUtcNow();
        this.SafePublishFaultedEvent(ex, now);

        // Slide the restart window: drop restarts older than the window, then record
        // this one.
        var windowStart = now - this.options.RestartWindow;
        this.restartTimes.RemoveAll(t => t < windowStart);
        this.restartTimes.Add(now);

        if (this.restartTimes.Count > this.options.MaxRestartsInWindow)
        {
            Volatile.Write(ref this.faultedState, 1);
            this.LogTickLoopGaveUp(this.options.MaxRestartsInWindow, this.options.RestartWindow.TotalSeconds);
            this.ReleaseFaultedBarrier();
        }
    }

    /// <summary>
    /// Awaits the loop transitioning to its faulted state, completing immediately when
    /// it is already faulted. Tests use this to deterministically observe the faulted
    /// transition without a real sleep.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the faulted transition.</param>
    /// <returns>A task that completes when the loop is faulted.</returns>
    [SuppressMessage(
        "Usage",
        "VSTHRD003:Avoid awaiting foreign Tasks",
        Justification = "The faulted barrier is an intentional cross-thread primitive " +
            "completed by the tick thread on the faulted transition.")]
    internal async Task WaitForFaultedAsync(TimeSpan timeout)
    {
        var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (this.faultedGate)
        {
            if (this.IsFaulted)
            {
                return;
            }

            this.pendingFaulted.Add(request);
        }

        var deadline = Task.Delay(timeout);
        var completed = await Task.WhenAny(request.Task, deadline).ConfigureAwait(false);
        if (completed == deadline)
        {
            throw new TimeoutException(
                $"The tick loop did not transition to faulted within {timeout}.");
        }
    }

    /// <summary>
    /// Completes and clears every pending faulted-barrier request. Called on the
    /// faulted transition.
    /// </summary>
    private void ReleaseFaultedBarrier()
    {
        lock (this.faultedGate)
        {
            foreach (var request in this.pendingFaulted)
            {
                request.TrySetResult();
            }

            this.pendingFaulted.Clear();
        }
    }

    /// <summary>
    /// Publishes a <see cref="SchedulerFaultedEvent"/>, swallowing a secondary fault
    /// from the sink so fault handling itself can never crash the recovery loop.
    /// </summary>
    /// <param name="ex">The fault to report.</param>
    /// <param name="faultedAt">The instant the fault occurred.</param>
    private void SafePublishFaultedEvent(Exception ex, DateTimeOffset faultedAt)
    {
        try
        {
            this.eventSink.Publish(new SchedulerFaultedEvent(ex, faultedAt));
        }
#pragma warning disable CA1031 // Fault reporting must not itself fault the recovery loop.
        catch (Exception)
#pragma warning restore CA1031
        {
            // The sink itself faulted; nothing more we can safely do here.
        }
    }

    /// <summary>
    /// Gracefully stops the loop (DR-10): stops scheduling new fires, then waits up to
    /// <see cref="SchedulerOptions.ShutdownTimeout"/> for the dispatches already handed
    /// off to the pool to complete, abandoning any that exceed the window so shutdown
    /// is never blocked indefinitely.
    /// </summary>
    /// <remarks>
    /// Idempotent and safe to call before the loop has fully started (NCronJob#172):
    /// stopping a never-started loop is a no-op, and a second stop returns immediately.
    /// </remarks>
    /// <param name="cancellationToken">A token to cancel the stop.</param>
    /// <returns>A task that completes when the loop has stopped.</returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Idempotent: a second stop (or a stop of a never-started loop) is a no-op.
        if (Interlocked.Exchange(ref this.stopState, 1) == 1)
        {
            return;
        }

        // Signal the loop to stop scheduling and let the base unwind ExecuteAsync. The
        // base call is guarded: it is a no-op when ExecuteAsync never started.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // Wait up to the shutdown window for in-flight dispatches handed to the pool to
        // finish; abandon any still running when the window elapses.
        await this.WaitForInFlightToDrainAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits up to <see cref="SchedulerOptions.ShutdownTimeout"/> for the in-flight
    /// dispatch count to reach zero, returning early when it drains and after the
    /// timeout otherwise (abandoning the stragglers). Time flows through the injected
    /// <see cref="TimeProvider"/> (DR-7).
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    /// <returns>A task that completes when in-flight drains or the timeout elapses.</returns>
    [SuppressMessage(
        "Usage",
        "VSTHRD003:Avoid awaiting foreign Tasks",
        Justification = "The drain signal is an intentional cross-thread primitive " +
            "completed when the last pool-thread dispatch finishes.")]
    private async Task WaitForInFlightToDrainAsync(CancellationToken cancellationToken)
    {
        var deadline = Task.Delay(this.options.ShutdownTimeout, this.timeProvider, cancellationToken);

        while (Volatile.Read(ref this.inFlightDispatches) > 0)
        {
            var drain = this.CurrentDrainTask();

            // Re-check after capturing the drain task to avoid a lost wakeup: a
            // dispatch that drained between the count read and here completed a
            // previous drain task, but the count is now zero so we exit.
            if (Volatile.Read(ref this.inFlightDispatches) == 0)
            {
                return;
            }

            try
            {
                var completed = await Task.WhenAny(drain, deadline).ConfigureAwait(false);
                if (completed == deadline)
                {
                    // Timed out: abandon the still-running dispatches.
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
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

        // DR-10 clock-skew guard: if the clock moved backwards since the last reading,
        // warn and continue. The loop always computes its sleep from a freshly read
        // 'now' (below), never from a stale absolute deadline, so a backwards jump just
        // re-arms a longer delay rather than losing or duplicating a fire.
        var previous = Volatile.Read(ref this.previousNowTicks);
        if (previous != long.MinValue && now.UtcTicks < previous)
        {
            this.LogClockSkew(new DateTimeOffset(previous, TimeSpan.Zero), now);
        }

        Volatile.Write(ref this.previousNowTicks, now.UtcTicks);

        var nextFire = this.PeekNextValidFire();

        // A due job (or a past-due one) means do not wait — loop straight back to
        // dispatch it.
        if (nextFire is not null && nextFire.Value <= now)
        {
            return;
        }

        // Arm every wait source BEFORE releasing the idle barrier. Ordering is
        // load-bearing: a test advances the FakeTimeProvider only after its
        // WaitForIdleAsync returns (which the barrier release completes), and the
        // advance fires only timers that already exist. Creating the Task.Delay first
        // guarantees the next-fire timer is armed before the test can advance the
        // clock, so the advance reliably wakes the loop (no missed wakeup).
        var drainTask = this.CurrentDrainTask();
        var commandReady = this.registry.Commands.WaitToReadAsync(stoppingToken).AsTask();
        var barrierWakeTask = this.CurrentBarrierWakeTask();
        Task? delayTask = null;
        if (nextFire is not null)
        {
            var delay = nextFire.Value - now;
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            delayTask = Task.Delay(delay, this.timeProvider, stoppingToken);
        }

        // Fully quiescent: nothing due and no dispatch in flight. Release the idle
        // barrier (after the timer is armed) so any waiting test observes the settled
        // state and can safely advance the clock. When a dispatch is in flight the loop
        // is not yet idle; the barrier is released at the next park after it drains.
        if (Volatile.Read(ref this.inFlightDispatches) == 0)
        {
            this.ReleaseIdleBarrier();
        }

        try
        {
            if (delayTask is null)
            {
                // Empty heap: wait on the command stream, dispatch drain, and barrier.
                await Task.WhenAny(commandReady, drainTask, barrierWakeTask).ConfigureAwait(false);
                return;
            }

            await Task.WhenAny(commandReady, delayTask, drainTask, barrierWakeTask).ConfigureAwait(false);
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

            // Re-schedule BEFORE dispatching so the next fire is committed even if the
            // dispatch or a fault in the dispatch path throws (DR-4/DR-10): a recurring
            // job never loses its schedule to a failed fire.
            if (nextNext is null)
            {
                // One-shot (or exhausted cadence): drop it from the live state.
                this.scheduled.Remove(handle.JobName);
            }
            else
            {
                this.Enqueue(handle.JobName, handle.Cadence, handle.MissedFirePolicy, nextNext.Value);
            }

            this.Dispatch(handle.JobName, scheduledOccurrence, nextNext);
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
        // must not undo it, fault the loop, or stall it.
        this.SafeCheckpoint(jobName, scheduledOccurrence, nextFireAt);
    }

    /// <summary>
    /// Checkpoints a fire to the store, best-effort (DR-4/DR-10). The fire and the
    /// next-fire schedule are already committed, so a checkpoint failure — whether the
    /// store throws synchronously or returns a faulted task — is logged at warning and
    /// swallowed. It never faults the loop or stalls it: the checkpoint is observed
    /// asynchronously off the tick thread so a slow store does not block scheduling.
    /// </summary>
    /// <param name="jobName">The job that fired.</param>
    /// <param name="firedAt">The fire instant.</param>
    /// <param name="nextFireAt">The next occurrence, or <see langword="null"/>.</param>
    [SuppressMessage(
        "Usage",
        "VSTHRD110:Observe result of async calls",
        Justification = "The checkpoint is intentionally best-effort and observed via " +
            "the ObserveCheckpointAsync continuation, which swallows failures.")]
    private void SafeCheckpoint(string jobName, DateTimeOffset firedAt, DateTimeOffset? nextFireAt)
    {
        ValueTask checkpoint;
        try
        {
            checkpoint = this.store.RecordFiredAsync(jobName, firedAt, nextFireAt, CancellationToken.None);
        }
#pragma warning disable CA1031 // Best-effort: a synchronous store throw must not fault the loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogCheckpointFailed(jobName, ex);
            return;
        }

        if (checkpoint.IsCompletedSuccessfully)
        {
            return;
        }

        // The checkpoint is in flight (or already faulted): observe it off the tick
        // thread so an async store fault is logged rather than left unobserved, and a
        // slow store never blocks scheduling.
        _ = this.ObserveCheckpointAsync(jobName, checkpoint);
    }

    /// <summary>
    /// Observes an in-flight checkpoint task, logging a warning if it faults so the
    /// failure is never silently unobserved.
    /// </summary>
    /// <param name="jobName">The job whose checkpoint is observed.</param>
    /// <param name="checkpoint">The in-flight checkpoint task.</param>
    /// <returns>A task that always completes successfully.</returns>
    private async Task ObserveCheckpointAsync(string jobName, ValueTask checkpoint)
    {
        try
        {
            await checkpoint.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best-effort: an async store fault must not propagate.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogCheckpointFailed(jobName, ex);
        }
    }

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
    /// Loads persisted jobs and seeds the heap. For each durable record that has a
    /// live dispatcher in the registry, the loop first reconciles the occurrences
    /// missed while the process was down — applying the record's
    /// <see cref="MissedFirePolicy"/> against its durable <c>LastFiredAt</c> (DR-3) —
    /// then arms the normal next fire.
    /// </summary>
    /// <remarks>
    /// The store supplies the durable timing state — cadence, <c>LastFiredAt</c>, and
    /// policy — and the registry supplies the live dispatcher; a record present in the
    /// store but absent from the registry has no dispatcher and is skipped. A job
    /// registered live this process (no prior fires, no durable record) is seeded
    /// from its registry record by <see cref="Arm"/> via the pre-start command drain.
    /// </remarks>
    /// <param name="stoppingToken">The loop's stopping token.</param>
    /// <returns>A task that completes when seeding is done.</returns>
    private async Task SeedAsync(CancellationToken stoppingToken)
    {
        var now = this.timeProvider.GetUtcNow();
        var persisted = await this.store.LoadAllAsync(stoppingToken).ConfigureAwait(false);

        var seeded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in persisted)
        {
            // Only running jobs with a live dispatcher are seedable from durable state.
            if (record.State != JobState.Running || this.registry.TryGetDispatcher(record.Name) is null)
            {
                continue;
            }

            this.ApplyStartupMissedFirePolicies(record, now);
            this.ArmFromRecord(record, now);
            seeded.Add(record.Name);
        }

        // Drain any commands posted before the loop started (live registrations made
        // during host construction) and arm any registered running job the store did
        // not already cover.
        this.DrainCommands();
        foreach (var record in this.registry.SnapshotRecords())
        {
            if (record.State == JobState.Running && !seeded.Contains(record.Name))
            {
                this.Arm(record.Name);
            }
        }
    }

    /// <summary>
    /// Reconciles the occurrences a job missed while the process was down, per its
    /// <see cref="MissedFirePolicy"/> (DR-3). Each catch-up occurrence is dispatched
    /// in order; when any were missed a <see cref="JobMissedFireEvent"/> reports the
    /// count and policy. The capacity cap bounds the backlog, and a capped
    /// <see cref="MissedFirePolicy.FireAllMissed"/> backlog is logged.
    /// </summary>
    /// <param name="record">The durable record carrying the job's last-fired instant.</param>
    /// <param name="now">The startup instant (DR-7).</param>
    private void ApplyStartupMissedFirePolicies(JobRecord record, DateTimeOffset now)
    {
        // The catch-up occurrences actually dispatched, per the configured policy:
        // the full (capped) backlog for FireAllMissed, a single coalesced fire for
        // Coalesce, and none for SkipMissed.
        var catchUp = MissedFirePolicyApplier.ComputeMissedFires(
            record.Cadence, record.LastFiredAt, now, record.MissedFirePolicy);

        // The true number of occurrences missed, independent of how the policy
        // reconciles them — so the event reports "5 missed, coalesced" rather than the
        // single dispatched fire.
        var missedCount = MissedFirePolicyApplier.ComputeMissedFires(
            record.Cadence, record.LastFiredAt, now, MissedFirePolicy.FireAllMissed).Count;

        if (missedCount == 0)
        {
            return;
        }

        if (missedCount >= MissedFirePolicyApplierCatchUpCap)
        {
            this.LogMissedFireCapReached(record.Name, MissedFirePolicyApplierCatchUpCap);
        }

        this.eventSink.Publish(new JobMissedFireEvent(record.Name, missedCount, record.MissedFirePolicy));

        // Dispatch each catch-up occurrence in order. The next fire after each is
        // computed strictly from the occurrence (not the wall clock), matching the
        // running-loop dispatch path.
        foreach (var occurrence in catchUp)
        {
            var nextAfter = record.Cadence.ComputeNextFire(occurrence, occurrence);
            this.Dispatch(record.Name, occurrence, nextAfter);
        }
    }

    /// <summary>
    /// Arms a job from a durable record: computes its next fire from the cadence and
    /// the durable last-fired instant and enqueues it, dropping it when no further
    /// occurrence is scheduled.
    /// </summary>
    /// <param name="record">The durable record.</param>
    /// <param name="now">The startup instant (DR-7).</param>
    private void ArmFromRecord(JobRecord record, DateTimeOffset now)
    {
        var nextFire = record.Cadence.ComputeNextFire(record.LastFiredAt, now);
        if (nextFire is null)
        {
            this.scheduled.Remove(record.Name);
            return;
        }

        this.Enqueue(record.Name, record.Cadence, record.MissedFirePolicy, nextFire.Value);
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
    /// Wraps the job's live dispatcher so the loop can count the dispatch as in-flight
    /// for the duration of the fire, and so a throwing dispatcher is surfaced as a
    /// <see cref="JobFireFailedEvent"/> here — <em>before</em> the in-flight count is
    /// cleared. Publishing the failure inside the <c>catch</c> (rather than letting it
    /// propagate to the router) guarantees the failed-fire event is observable by the
    /// time the loop next reports idle, and avoids a double publish. The decorator then
    /// completes normally, so the router sees success. The <c>finally</c> clears the
    /// in-flight count exactly once per fire.
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
#pragma warning disable CA1031 // A throwing dispatcher is isolated as a failed-fire event.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // Isolate the dispatcher (DR-4): surface the throw as a failed fire
                // carrying the exception, published before the in-flight count clears.
                owner.eventSink.Publish(new JobFireFailedEvent(
                    context.JobName,
                    context.FireTime,
                    ex));
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
