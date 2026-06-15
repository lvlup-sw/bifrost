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
using Bifrost.Scheduling.Observability;
using Bifrost.Scheduling.Registry;

using Microsoft.Extensions.DependencyInjection;
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
public sealed partial class ScheduleTickLoop : BackgroundService, IDisposable, ISchedulerFaultSource
{
    /// <summary>
    /// The maximum number of missed occurrences reconciled at startup — mirrors the
    /// default cap of <see cref="MissedFirePolicyApplier.ComputeMissedFires"/>, used to
    /// detect and log a capped <see cref="MissedFirePolicy.FireAllMissed"/> backlog.
    /// </summary>
    private const int MissedFirePolicyApplierCatchUpCap = 100;

    /// <summary>
    /// The <c>exception.type</c> tag value used for a dispatch failure that carries
    /// no exception — the job had no live dispatcher at the moment of fire.
    /// </summary>
    private const string NoDispatcherFailureType = "NoDispatcher";

    private readonly ScheduleRegistry registry;
    private readonly IScheduleStore store;
    private readonly TimeProvider timeProvider;
    private readonly IJobDispatcherRouter router;
    private readonly ISchedulerEventSink eventSink;
    private readonly ILogger<ScheduleTickLoop> logger;
    private readonly SchedulerOptions options;
    private readonly SchedulerMetrics metrics;
    private readonly ITickHealthMonitor healthMonitor;

    // The root scope factory every fire opens a per-fire IServiceScope from, so a
    // dispatcher can resolve scoped work via JobFireContext.Services (F2/M2). Null when
    // no root provider was supplied (a few low-level fixtures); such a fire falls back
    // to EmptyServiceProvider. The scope outlives the tick-thread handoff: it is
    // disposed in the dispatch continuation once the pool-thread fire completes, never
    // synchronously on the tick thread.
    private readonly IServiceScopeFactory? scopeFactory;

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

    // The cached command-readiness wait, reused across wakes to avoid a per-wake
    // WaitToReadAsync().AsTask() allocation on the hot non-command wake path (core-2).
    // The channel is SingleReader (only the tick thread reads it), so a single
    // outstanding waiter is correct. It is recreated only once it has completed — i.e.
    // a command became available — so a non-command wake (delay/drain/barrier) reuses
    // the same still-pending task. A fresh WaitToReadAsync observes any command already
    // queued and completes immediately, so no wakeup is ever lost.
    private Task<bool>? commandReadyTask;

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
    /// <param name="metrics">
    /// The scheduler metrics the loop records fires, fire latency, missed-fire
    /// reconciliation, and dispatch failures against (DR-8). When
    /// <see langword="null"/>, a private meter is created; production passes the
    /// shared instance the registry also records against.
    /// </param>
    /// <param name="healthMonitor">
    /// The tick/fire liveness monitor the loop updates so the
    /// <see cref="SchedulerHealthCheck"/> can read it (DR-8): the loop records a tick
    /// each wait and a fire outcome (success or failure) per dispatch. When
    /// <see langword="null"/>, a private monitor is created; production passes the
    /// shared instance the health check reads.
    /// </param>
    /// <param name="serviceProvider">
    /// The root service provider each fire opens a per-fire <see cref="IServiceScope"/>
    /// from, surfaced to the dispatcher as
    /// <see cref="Bifrost.Scheduling.Core.JobFireContext.Services"/> (F2/M2). DI resolves
    /// the application root provider here; the loop creates one scope per fire and
    /// disposes it once the (pool-thread) dispatch completes. When <see langword="null"/>
    /// — a few low-level fixtures with no provider — a fire falls back to a no-op
    /// provider so scoped resolution returns <see langword="null"/> rather than throwing.
    /// </param>
    internal ScheduleTickLoop(
        ScheduleRegistry registry,
        IScheduleStore store,
        TimeProvider timeProvider,
        IJobDispatcherRouter router,
        ISchedulerEventSink eventSink,
        ILogger<ScheduleTickLoop> logger,
        SchedulerOptions options,
        SchedulerMetrics? metrics = null,
        ITickHealthMonitor? healthMonitor = null,
        IServiceProvider? serviceProvider = null)
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
        this.metrics = metrics ?? new SchedulerMetrics();
        this.healthMonitor = healthMonitor ?? new TickHealthMonitor();

        // Resolve the scope factory once from the root provider so every fire can open a
        // cheap per-fire scope without re-resolving. Falls back to null when no provider
        // is supplied or the factory is absent (a bare provider in a fixture).
        this.scopeFactory = serviceProvider?.GetService<IServiceScopeFactory>();
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

        // Real-wall-clock guard against a test hang — NOT a scheduled tick. The
        // barrier completes on the tick thread; this deadline only bounds the wait, so
        // it must advance on the system clock (TimeProvider.System) regardless of any
        // injected fake clock. The TimeProvider overload also keeps this off the
        // banned-API list (RS0030), which bars the bare Task.Delay(TimeSpan).
        var deadline = Task.Delay(timeout, TimeProvider.System, CancellationToken.None);
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

    /// <inheritdoc/>
    bool ISchedulerFaultSource.IsFaulted => this.IsFaulted;

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

        // Real-wall-clock guard against a test hang — NOT a scheduled tick. The
        // barrier completes on the tick thread; this deadline only bounds the wait, so
        // it must advance on the system clock (TimeProvider.System) regardless of any
        // injected fake clock. The TimeProvider overload also keeps this off the
        // banned-API list (RS0030), which bars the bare Task.Delay(TimeSpan).
        var deadline = Task.Delay(timeout, TimeProvider.System, CancellationToken.None);
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

        // Arm the shutdown-window deadline BEFORE the first await. Ordering is
        // load-bearing (it mirrors the wake-timer rule in WaitForNextWakeAsync): a test
        // advances the FakeTimeProvider only after StopAsync has yielded, and an advance
        // fires only timers that already exist. Creating the deadline synchronously here
        // — before base.StopAsync unwinds the loop — guarantees it is registered before
        // the clock can be advanced, so the timeout reliably elapses instead of racing
        // the loop teardown (otherwise the drain wait can park on a timer that is never
        // fired, hanging StopAsync indefinitely under a fake clock).
        var deadline = Task.Delay(this.options.ShutdownTimeout, this.timeProvider, cancellationToken);

        // Signal the loop to stop scheduling and let the base unwind ExecuteAsync. The
        // base call is guarded: it is a no-op when ExecuteAsync never started.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // Wait up to the shutdown window for in-flight dispatches handed to the pool to
        // finish; abandon any still running when the window elapses.
        await this.WaitForInFlightToDrainAsync(deadline).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits up to <see cref="SchedulerOptions.ShutdownTimeout"/> for the in-flight
    /// dispatch count to reach zero, returning early when it drains and after the
    /// timeout otherwise (abandoning the stragglers). The timeout deadline is armed by
    /// the caller before its first await (see <see cref="StopAsync"/>) so it is
    /// registered on the injected <see cref="TimeProvider"/> (DR-7) before a test can
    /// advance a fake clock past it.
    /// </summary>
    /// <param name="deadline">The pre-armed shutdown-window deadline task.</param>
    /// <returns>A task that completes when in-flight drains or the timeout elapses.</returns>
    [SuppressMessage(
        "Usage",
        "VSTHRD003:Avoid awaiting foreign Tasks",
        Justification = "The drain signal is an intentional cross-thread primitive " +
            "completed when the last pool-thread dispatch finishes.")]
    private async Task WaitForInFlightToDrainAsync(Task deadline)
    {
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
        // Record liveness each iteration so the health check can detect a stalled
        // loop (DR-8). The instant is read from the injected clock (DR-7).
        this.healthMonitor.RecordTick(this.timeProvider.GetUtcNow());

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
        var commandReady = this.CommandReadyTask(stoppingToken);
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
    /// Returns the command-readiness wait, reused across wakes to avoid a per-wake
    /// <c>WaitToReadAsync().AsTask()</c> allocation on the hot non-command wake path
    /// (core-2). A fresh wait is created only when the cached one is absent or has
    /// already completed — meaning a command became available since it was created. The
    /// channel is single-reader (only the tick thread reads it), so one outstanding
    /// waiter is correct; a non-command wake (delay/drain/barrier) leaves the wait
    /// pending and reuses it next iteration. A freshly created <c>WaitToReadAsync</c>
    /// observes any command already queued and completes synchronously, so a command
    /// posted between a drain and this call still wakes the loop — no lost wakeup.
    /// </summary>
    /// <param name="stoppingToken">The loop's stopping token.</param>
    /// <returns>The cached or freshly created command-readiness task.</returns>
    private Task<bool> CommandReadyTask(CancellationToken stoppingToken)
    {
        var cached = this.commandReadyTask;
        if (cached is null || cached.IsCompleted)
        {
            cached = this.registry.Commands.WaitToReadAsync(stoppingToken).AsTask();
            this.commandReadyTask = cached;
        }

        return cached;
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
    /// <remarks>
    /// When the persisted <c>NextFireAt</c> is still in the future it is used
    /// directly (optimisation: no cadence computation needed). When it is absent or
    /// in the past — for example after a pause across N occurrences — the cadence's
    /// next occurrence is computed from <c>(LastFiredAt, now)</c> so the loop arms
    /// at the next <em>natural</em> future occurrence rather than replaying stale
    /// past instants as if they were missed fires (DR-10: missed-fire catch-up is
    /// startup-from-store recovery only, not live resume).
    /// </remarks>
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

        // Use the stored NextFireAt only when it is still in the future; if it is
        // stale (in the past or absent) compute the next natural occurrence from now.
        // This ensures a resume after a long pause arms at the next future occurrence
        // rather than treating the intervening period as missed fires (DR-10).
        DateTimeOffset? nextFire;
        if (record.NextFireAt is not null && record.NextFireAt.Value > now)
        {
            nextFire = record.NextFireAt.Value;
        }
        else
        {
            nextFire = record.Cadence.ComputeNextFire(record.LastFiredAt, now);
        }

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
            // SafeComputeNextFire wraps the call: a throwing cadence marks the job
            // Faulted and publishes a JobFireFailedEvent, isolating the fault so other
            // jobs continue to fire (DR-10, Task 48, Hangfire#529/#530/#537).
            var nextNext = this.SafeComputeNextFire(handle, scheduledOccurrence);

            // A null result from SafeComputeNextFire either means the cadence is
            // exhausted (normal) or it threw (faulted): in both cases the job is
            // dropped from the live schedule. The job was already marked Faulted
            // and its failed-fire event was published by SafeComputeNextFire.
            if (nextNext is null)
            {
                // One-shot, exhausted cadence, or faulted cadence: drop live state.
                this.scheduled.Remove(handle.JobName);

                // If the cadence faulted (marked via registry), do not dispatch the
                // (now un-schedulable) fire occurrence: the JobFireFailedEvent from
                // SafeComputeNextFire already reported the failure.
                var record = this.registry.TryGetRecord(handle.JobName);
                if (record?.State == JobState.Faulted)
                {
                    continue;
                }
            }
            else
            {
                this.Enqueue(handle.JobName, handle.Cadence, handle.MissedFirePolicy, nextNext.Value);
            }

            this.Dispatch(handle.JobName, scheduledOccurrence, nextNext);
        }
    }

    /// <summary>
    /// Computes the next occurrence for a job whose cadence may throw, isolating any
    /// fault as a <see cref="Core.Events.JobFireFailedEvent"/> and marking the job
    /// <see cref="JobState.Faulted"/> so it does not block the loop from processing
    /// other jobs (DR-10, Task 48, Hangfire#529/#530/#537).
    /// </summary>
    /// <remarks>
    /// A faulted job is removed from the live schedule by the caller
    /// (<see cref="DispatchDueJobs"/>); no further occurrences are enqueued for it.
    /// </remarks>
    /// <param name="handle">The heap handle for the job being evaluated.</param>
    /// <param name="scheduledOccurrence">The occurrence that just fired (the anchor for the next-fire computation).</param>
    /// <returns>
    /// The next scheduled occurrence, or <see langword="null"/> when the cadence is
    /// exhausted or threw (the job is faulted in the latter case).
    /// </returns>
#pragma warning disable CA1031 // A throwing cadence must be isolated, not allowed to crash the loop.
    private DateTimeOffset? SafeComputeNextFire(JobHandle handle, DateTimeOffset scheduledOccurrence)
    {
        try
        {
            return handle.Cadence.ComputeNextFire(scheduledOccurrence, scheduledOccurrence);
        }
        catch (Exception ex)
        {
            // The cadence's ComputeNextFire threw: isolate the fault. Mark the job
            // Faulted in the registry so it is not re-armed, and publish a
            // JobFireFailedEvent so observers can detect the per-job failure without
            // it being confused with a dispatched-but-throwing fire.
            this.registry.MarkJobFaulted(handle.JobName);
            this.eventSink.Publish(new Core.Events.JobFireFailedEvent(
                handle.JobName,
                scheduledOccurrence,
                ex,
                Reason: "Cadence.ComputeNextFire threw; job marked Faulted."));
            this.LogCadenceComputeNextFireFailed(handle.JobName, ex);
            return null;
        }
    }
#pragma warning restore CA1031

    /// <summary>
    /// Performs a single fire: resolves the live dispatcher, publishes
    /// <see cref="JobFiredEvent"/> — the dispatch-handoff signal — and checkpoints the
    /// fire best-effort, then opens a per-fire <see cref="IServiceScope"/> (F2/M2) and
    /// hands the fire to the router. The fire instant is the scheduled occurrence — never
    /// a wall-clock read — so it is stable across re-evaluation and serves as the
    /// idempotency key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Event ordering (core-1): <see cref="JobFiredEvent"/> is published before
    /// <c>router.Dispatch</c> so the handoff signal always precedes any per-fire outcome
    /// event a fast-failing dispatcher emits (a synchronous throw or an admission
    /// rejection), keeping the <c>(JobName, FireTime)</c> timeline monotonic for a
    /// correlating subscriber.
    /// </para>
    /// <para>
    /// Scope lifetime (F2/M2): the per-fire scope is created here on the tick thread but
    /// handed to the in-flight tracking decorator, which disposes it in its <c>finally</c>
    /// once the pool-thread dispatch completes — so the scope outlives the fire-and-forget
    /// handoff and a dispatcher can resolve scoped services through
    /// <see cref="JobFireContext.Services"/> for the full duration of the fire.
    /// </para>
    /// </remarks>
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
            this.metrics.RecordDispatchFailure(jobName, NoDispatcherFailureType);

            // Record the failed fire in the same health monitor the post-dispatch path
            // uses (InFlightTrackingDispatcher), so a pre-dispatch failure here is not
            // missing from FailureRate/RecentFireCount. The in-flight tracking dispatcher
            // never runs on this path, so this is the only place the outcome is recorded
            // (no double-recording).
            this.healthMonitor.RecordFireOutcome(success: false);
            return;
        }

        // Publish the handoff signal FIRST (core-1): a fast-failing dispatcher publishing
        // JobFireFailedEvent must never precede this event for the same occurrence. This
        // runs before the in-flight count is incremented and before any scope is created,
        // so if the publish itself faults the loop body there is no in-flight leak and no
        // orphaned scope (the original ordering incremented in-flight first, which would
        // strand the count and hang the idle barrier when the publish threw).
        this.eventSink.Publish(new JobFiredEvent(jobName, scheduledOccurrence, nextFireAt));

        // Fire latency is the gap between the scheduled occurrence (the logical fire
        // instant) and the actual dispatch instant. A fire dispatched on time or
        // early (a manual trigger) yields a non-positive gap the metric clamps to
        // zero. Time flows through the injected TimeProvider (DR-7).
        var dispatchedAt = this.timeProvider.GetUtcNow();
        this.metrics.RecordFired(jobName, dispatchedAt - scheduledOccurrence);

        // Best-effort checkpoint: the fire already happened, so a checkpoint failure
        // must not undo it, fault the loop, or stall it.
        this.SafeCheckpoint(jobName, scheduledOccurrence, nextFireAt);

        // Open a per-fire DI scope (F2/M2), now that the handoff signal has been
        // published. When no root provider was supplied the scope is null and the fire
        // falls back to the no-op provider, so scoped resolution returns null rather
        // than throwing. The scope is disposed by the tracking decorator after the
        // pool-thread dispatch completes (see InFlightTrackingDispatcher).
        IServiceScope? scope;
        try
        {
            scope = this.scopeFactory?.CreateScope();
        }
#pragma warning disable CA1031 // A scope-creation fault is surfaced as a failed fire, not a loop fault.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Scope creation can fault only when the root provider is already disposed
            // (e.g. mid-shutdown). The handoff JobFiredEvent was already published, so
            // publish a compensating JobFireFailedEvent to keep the (JobName, FireTime)
            // timeline complete — Fired then Failed — rather than leaving an orphan Fired
            // with no outcome. No scope or in-flight count exists yet, so nothing to unwind,
            // and the loop is not faulted by an otherwise-benign shutdown race.
            this.eventSink.Publish(new JobFireFailedEvent(jobName, scheduledOccurrence, ex));
            this.metrics.RecordDispatchFailure(jobName, ex.GetType().Name);

            // Record the failed fire in the same health monitor the post-dispatch path
            // uses (InFlightTrackingDispatcher): the scope-creation fault happens before
            // the tracking dispatcher runs, so without this the failed fire would be
            // missing from FailureRate/RecentFireCount. No tracking dispatcher runs on
            // this path, so this is the only recording (no double-recording).
            this.healthMonitor.RecordFireOutcome(success: false);
            return;
        }

        var services = scope?.ServiceProvider ?? EmptyServiceProvider.Instance;

        var context = new JobFireContext(
            jobName,
            scheduledOccurrence,
            nextFireAt,
            services);

        // Track the dispatch as in-flight before handing it to the router, and clear
        // the count when the wrapped dispatcher completes (success or throw). The
        // router runs the dispatch on the pool; the decorator's finally still runs
        // even when the router catches a throwing dispatcher, so the count is exact and
        // the per-fire scope is disposed exactly once after the fire finishes. The
        // increment is paired with the handoff: if the handoff itself were to throw
        // (it does not in practice), the catch undoes the count and disposes the scope
        // so the idle barrier can never strand.
        Interlocked.Increment(ref this.inFlightDispatches);
        var tracked = new InFlightTrackingDispatcher(this, dispatcher, scope);
        try
        {
            this.router.Dispatch(tracked, context, CancellationToken.None);
        }
#pragma warning disable CA1031 // Undo the in-flight bookkeeping on any handoff fault.
        catch
#pragma warning restore CA1031
        {
            // The router never throws in practice, but if the handoff faults the wrapped
            // dispatcher may never run its finally, so complete the fire here to keep the
            // in-flight bookkeeping exact and the idle barrier unstranded, then re-raise so
            // the loop's fault recovery still sees it. CompleteFromHandoffFault shares the
            // decorator's single-shot guard, so even a non-conforming router that both
            // started the dispatch AND threw cannot double-decrement the count.
            tracked.CompleteFromHandoffFault(jobName);
            throw;
        }
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
        this.metrics.RecordMissedFires(record.Name, missedCount, record.MissedFirePolicy);

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
    /// provider when no root provider is available to open a per-fire scope from (a few
    /// low-level fixtures). Returns <see langword="null"/> for every service, so a
    /// dispatcher's scoped resolution degrades to null rather than throwing.
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
    /// completes normally, so the router sees success. The <c>finally</c> disposes the
    /// per-fire scope and clears the in-flight count exactly once per fire — both on the
    /// pool thread after the dispatch finishes, so the scope outlives the fire-and-forget
    /// handoff (F2/M2).
    /// </summary>
    /// <param name="owner">The loop tracking the in-flight count.</param>
    /// <param name="inner">The job's live dispatcher.</param>
    /// <param name="scope">
    /// The per-fire DI scope to dispose after the fire completes, or
    /// <see langword="null"/> when no scope was created.
    /// </param>
    private sealed class InFlightTrackingDispatcher(ScheduleTickLoop owner, IJobDispatcher inner, IServiceScope? scope)
        : IJobDispatcher
    {
        // Single-shot completion guard (0 = not completed). Both the pool-thread finally
        // below and the synchronous handoff-fault path in Dispatch funnel through it, so
        // the per-fire scope is disposed and the in-flight count cleared EXACTLY once even
        // if a non-conforming IJobDispatcherRouter both starts the dispatch and then throws
        // synchronously. Without this a double completion would underflow inFlightDispatches
        // and strand the idle/drain barrier. The in-tree router never throws synchronously,
        // so only one path ever runs in practice.
        private int completed;

        public async ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            try
            {
                await inner.DispatchAsync(context, ct).ConfigureAwait(false);
                owner.healthMonitor.RecordFireOutcome(success: true);
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
                owner.metrics.RecordDispatchFailure(context.JobName, ex.GetType().Name);
                owner.healthMonitor.RecordFireOutcome(success: false);
            }
            finally
            {
                // Dispose the per-fire scope first so scoped IAsyncDisposable/IDisposable
                // services are released before the fire is reported drained, then clear
                // the in-flight count. A scope dispose must never crash the fire path.
                // Guarded so completion runs exactly once across this finally and the
                // handoff-fault path (see the field comment).
                if (Interlocked.Exchange(ref this.completed, 1) == 0)
                {
                    await this.DisposeScopeAsync(context.JobName).ConfigureAwait(false);
                    owner.OnDispatchCompleted();
                }
            }
        }

        /// <summary>
        /// Completes the fire synchronously from the handoff-fault path in
        /// <see cref="Dispatch"/> — disposes the per-fire scope and clears the in-flight
        /// count — when the router throws before the pool dispatch could run (or a
        /// non-conforming router throws after starting it). Idempotent with the
        /// pool-thread <c>finally</c> via the single-shot guard, so the count is
        /// decremented exactly once regardless of which path observes completion first.
        /// </summary>
        /// <param name="jobName">The job whose fire is being completed.</param>
        public void CompleteFromHandoffFault(string jobName)
        {
            if (Interlocked.Exchange(ref this.completed, 1) != 0)
            {
                return;
            }

            try
            {
                scope?.Dispose();
            }
#pragma warning disable CA1031 // A scope-dispose fault must not mask the handoff fault.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                owner.LogScopeDisposeFailed(jobName, ex);
            }

            owner.OnDispatchCompleted();
        }

        /// <summary>
        /// Disposes the per-fire scope, preferring async disposal so a scoped
        /// <see cref="IAsyncDisposable"/> service releases cleanly. A dispose fault is
        /// logged and swallowed so cleanup never crashes the isolated fire path.
        /// </summary>
        /// <param name="jobName">The job whose fire scope is being disposed.</param>
        /// <returns>A task that completes when the scope is disposed.</returns>
        private async ValueTask DisposeScopeAsync(string jobName)
        {
            try
            {
                if (scope is IAsyncDisposable asyncScope)
                {
                    await asyncScope.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    scope?.Dispose();
                }
            }
#pragma warning disable CA1031 // A scope-dispose fault must not crash the isolated fire path.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                owner.LogScopeDisposeFailed(jobName, ex);
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
