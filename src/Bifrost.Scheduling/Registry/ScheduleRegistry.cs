// =============================================================================
// <copyright file="ScheduleRegistry.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Observability;

namespace Bifrost.Scheduling.Registry;

/// <summary>
/// The default <see cref="IScheduleRegistry"/>: the live, in-process control
/// surface for scheduled jobs. It owns job metadata and the live dispatcher per
/// job, persists every mutation through an <see cref="IScheduleStore"/>, and posts
/// a <see cref="RegistryCommand"/> to its wake channel so the tick loop can
/// re-evaluate the affected job.
/// </summary>
/// <remarks>
/// Durable state flows through the store. Pause, resume, and removal write through
/// before the in-memory state is mutated, so a store failure rolls back the change
/// rather than leaving the registry and store divergent. Registration is the one
/// exception to that order: it claims the in-memory name slot first, then persists,
/// and compensates by releasing the slot if the store write fails. Claiming first is
/// what keeps a losing concurrent same-name registrant from ever writing its payload
/// through the store's blind upsert, while the compensation preserves the same
/// store-failure-rolls-back invariant. Names are the identity key and are validated
/// against a compiled lowercase pattern.
/// </remarks>
public sealed partial class ScheduleRegistry : IScheduleRegistry, IAsyncDisposable, IDisposable
{
    private readonly JobMutationGates mutationGates = new();
    private readonly ConditionalWeakTable<JobRecord, JobRecord> checkpointOrigins = new();
    private readonly ConditionalWeakTable<JobRecord, CheckpointProgress> checkpointProgress = new();
    private readonly IScheduleStore store;
    private readonly TimeProvider timeProvider;
    private readonly SchedulerMetrics metrics;
    private readonly ISchedulerEventSink events;
    private readonly ConcurrentDictionary<string, JobRecord> jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IJobDispatcher> dispatchers = new(StringComparer.Ordinal);
    private readonly object registrationGate = new();
    private readonly Dictionary<string, long> registrationIds = new(StringComparer.Ordinal);
    private long nextRegistrationId;

    /// <summary>
    /// Maximum triggers accepted and not yet handed off. Further
    /// <see cref="TriggerAsync"/> calls wait, so the command channel cannot
    /// accumulate an unbounded trigger backlog.
    /// </summary>
    internal const int MaxQueuedTriggers = 64;

    private readonly Channel<RegistryCommand> commands =
        Channel.CreateUnbounded<RegistryCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly SemaphoreSlim triggerAdmission = new(MaxQueuedTriggers, MaxQueuedTriggers);

    private readonly CancellationTokenSource triggerAdmissionCancelled = new();

    // Single-shot dispose guard (0 = not disposed). Interlocked so a concurrent or
    // repeated dispose completes the command writer exactly once: a second
    // Writer.Complete() throws ChannelClosedException, so the guard makes disposal
    // idempotent across both the sync (Dispose) and async (DisposeAsync) paths.
    private int disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScheduleRegistry"/> class.
    /// </summary>
    /// <param name="store">The durable persistence port for scheduled jobs.</param>
    /// <param name="timeProvider">
    /// The clock used to compute a job's initial next-fire instant at registration
    /// (DR-7); the registry never reads a clock of its own.
    /// </param>
    /// <param name="metrics">
    /// The scheduler metrics the registry records job registration and removal
    /// against (DR-8). When <see langword="null"/>, a private meter is created so the
    /// registry can be constructed without observability wiring; production passes the
    /// shared instance the tick loop also records against.
    /// </param>
    /// <param name="events">
    /// The event sink the registry publishes lifecycle events through (DR-8):
    /// <see cref="JobRegisteredEvent"/>, <see cref="JobUnregisteredEvent"/>,
    /// <see cref="JobPausedEvent"/>, and <see cref="JobResumedEvent"/>. When
    /// <see langword="null"/>, a no-op sink is used; production passes the same
    /// <see cref="SchedulerEventStream"/> the tick loop publishes fire and fault
    /// events through, so a subscriber observes one unified timeline.
    /// </param>
    public ScheduleRegistry(
        IScheduleStore store,
        TimeProvider timeProvider,
        SchedulerMetrics? metrics = null,
        ISchedulerEventSink? events = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.store = store;
        this.timeProvider = timeProvider;
        this.metrics = metrics ?? new SchedulerMetrics();
        this.events = events ?? NullSchedulerEventSink.Instance;
    }

    /// <summary>
    /// Gets the reader over the wake channel: the tick loop drains this to learn of
    /// registration, removal, pause, resume, and trigger actions as they happen.
    /// </summary>
    internal ChannelReader<RegistryCommand> Commands => this.commands.Reader;

    /// <summary>
    /// Returns the current persisted <see cref="JobRecord"/> for a job, or
    /// <see langword="null"/> when no job is registered under that name. The tick
    /// loop reads this to learn a job's cadence, missed-fire policy, and state when
    /// it processes a wake command. This is an internal read accessor — it is not
    /// part of the public <see cref="IScheduleRegistry"/> surface.
    /// </summary>
    /// <param name="name">The job name to look up.</param>
    /// <returns>The job's current record, or <see langword="null"/> if absent.</returns>
    internal JobRecord? TryGetRecord(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return this.jobs.TryGetValue(name, out var record) ? record : null;
    }

    /// <summary>
    /// Returns the live <see cref="IJobDispatcher"/> registered for a job, or
    /// <see langword="null"/> when none is attached. The tick loop resolves a job's
    /// dispatcher through this accessor at the moment of dispatch, so a re-registered
    /// dispatcher is always honoured. This is an internal read accessor.
    /// </summary>
    /// <param name="name">The job name whose dispatcher to resolve.</param>
    /// <returns>The job's live dispatcher, or <see langword="null"/> if absent.</returns>
    internal IJobDispatcher? TryGetDispatcher(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return this.dispatchers.TryGetValue(name, out var dispatcher) ? dispatcher : null;
    }

    /// <summary>
    /// Binds <paramref name="dispatcher"/> to a new registration of <paramref name="name"/>.
    /// </summary>
    /// <param name="name">The job name.</param>
    /// <param name="dispatcher">The dispatcher for this registration.</param>
    /// <returns>The registration id.</returns>
    internal long BindDispatcher(string name, IJobDispatcher dispatcher)
    {
        lock (this.registrationGate)
        {
            var id = ++this.nextRegistrationId;
            this.registrationIds[name] = id;
            this.dispatchers[name] = dispatcher;
            return id;
        }
    }

    /// <summary>
    /// Drops the dispatcher and registration id for <paramref name="name"/>.
    /// </summary>
    /// <param name="name">The job name.</param>
    internal void UnbindDispatcher(string name)
    {
        lock (this.registrationGate)
        {
            this.registrationIds.Remove(name);
            this.dispatchers.TryRemove(name, out _);
        }
    }

    /// <summary>The registration id currently bound to <paramref name="name"/>, or zero.</summary>
    /// <param name="name">The job name.</param>
    /// <returns>The live registration id.</returns>
    internal long RegistrationId(string name)
    {
        lock (this.registrationGate)
        {
            return this.registrationIds.GetValueOrDefault(name);
        }
    }

    /// <summary>
    /// Resolves the dispatcher for one registration.
    /// A newer registration of the same name is reported as replaced.
    /// </summary>
    /// <param name="name">The job name.</param>
    /// <param name="registrationId">The registration that queued the fire.</param>
    /// <returns>The bound dispatcher, or a miss.</returns>
    internal DispatcherResolution ResolveDispatcher(string name, long registrationId)
    {
        lock (this.registrationGate)
        {
            var current = this.registrationIds.GetValueOrDefault(name);
            this.dispatchers.TryGetValue(name, out var dispatcher);
            if (registrationId != 0 && current == registrationId && dispatcher is not null)
            {
                return new DispatcherResolution(dispatcher, Replaced: false);
            }

            var replaced = dispatcher is not null && current != registrationId;
            return new DispatcherResolution(null, replaced);
        }
    }

    /// <summary>
    /// Enumerates a snapshot of every currently registered job's record. The tick
    /// loop uses this when seeding its heap at startup to reconcile store-loaded jobs
    /// against the live, dispatcher-bearing registry. This is an internal read
    /// accessor.
    /// </summary>
    /// <returns>A snapshot of all registered job records.</returns>
    internal IReadOnlyList<JobRecord> SnapshotRecords() => [.. this.jobs.Values];

    /// <summary>
    /// Marks a job as <see cref="JobState.Faulted"/> in-process, called by the tick
    /// loop when a per-job <c>ComputeNextFire</c> throws (DR-10, Task 48). The
    /// in-memory state is updated synchronously on the tick thread; the store write
    /// is intentionally skipped here — the store is a best-effort checkpoint and
    /// the fault is visible in-process immediately. Silently ignores unknown jobs.
    /// </summary>
    /// <param name="name">The job name to mark faulted.</param>
    internal void MarkJobFaulted(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!this.jobs.TryGetValue(name, out var record))
        {
            return;
        }

        this.jobs[name] = record with { State = JobState.Faulted };
    }

    /// <summary>
    /// Returns the next <paramref name="count"/> future fire instants for the named
    /// job by iterating the cadence's <see cref="Cadence.ComputeNextFire"/> — the
    /// same engine the tick loop uses (DR-8/R10). The registry's injected
    /// <see cref="TimeProvider"/> supplies <c>now</c> so that the preview is
    /// consistent with what the loop would compute at this moment.
    /// </summary>
    /// <param name="name">The job name to project.</param>
    /// <param name="count">The number of occurrences to return.</param>
    /// <returns>
    /// An ascending list of future instants; may be shorter than <paramref name="count"/>
    /// when the cadence is exhausted, and empty when no job is registered under
    /// <paramref name="name"/>.
    /// </returns>
    internal IReadOnlyList<DateTimeOffset> GetNextOccurrences(string name, int count)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1, nameof(count));

        if (!this.jobs.TryGetValue(name, out var record))
        {
            return [];
        }

        var result = new List<DateTimeOffset>(count);
        var cadence = record.Cadence;
        // Preserve the occurrence already scheduled by registration or the tick loop.
        DateTimeOffset? lastFiredAt = record.LastFiredAt;
        var now = this.timeProvider.GetUtcNow();

        for (int i = 0; i < count; i++)
        {
            var next = i == 0 && record.NextFireAt > now
                ? record.NextFireAt
                : cadence.ComputeNextFire(lastFiredAt, now);
            if (next is null || next <= now)
            {
                break;
            }

            result.Add(next.Value);
            // The next iteration's "lastFiredAt" is this occurrence, and we advance
            // "now" past it so that ComputeNextFire always projects strictly forward.
            lastFiredAt = next.Value;
            now = next.Value;
        }

        return result;
    }

    /// <inheritdoc/>
    public ValueTask RegisterAsync(string name, Cadence cadence, MissedFirePolicy missedFirePolicy, IJobDispatcher dispatcher, CancellationToken ct = default)
        => this.MutateAsync(name, () => this.RegisterCoreAsync(name, cadence, missedFirePolicy, dispatcher, ct), ct);

    /// <inheritdoc/>
    public async ValueTask<bool> UnregisterAsync(string name, CancellationToken ct = default)
    {
        this.ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(name);
        using var lease = await this.mutationGates.EnterAsync(name, ct).ConfigureAwait(false);
        return await this.UnregisterCoreAsync(name, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ValueTask PauseAsync(string name, CancellationToken ct = default)
        => this.MutateAsync(name, () => this.PauseCoreAsync(name, ct), ct);

    /// <inheritdoc/>
    public ValueTask ResumeAsync(string name, CancellationToken ct = default)
        => this.MutateAsync(name, () => this.ResumeCoreAsync(name, ct), ct);

    /// <inheritdoc/>
    public ValueTask UpdateAsync(string name, Cadence cadence, MissedFirePolicy missedFirePolicy, CancellationToken ct = default)
        => this.MutateAsync(name, () => this.UpdateCoreAsync(name, cadence, missedFirePolicy, ct), ct);

    internal async ValueTask CheckpointAsync(JobRecord expected, DateTimeOffset firedAt, DateTimeOffset? nextFireAt)
    {
        using var lease = await this.mutationGates.EnterAsync(expected.Name).ConfigureAwait(false);
        if (this.jobs.TryGetValue(expected.Name, out var current)
            && ReferenceEquals(this.CheckpointOrigin(current), this.CheckpointOrigin(expected)))
        {
            var progress = this.checkpointProgress.GetValue(this.CheckpointOrigin(current), static _ => new CheckpointProgress());
            if ((current.LastFiredAt is { } recorded && firedAt <= recorded)
                || (progress.HighestAttempt is { } attempted && firedAt < attempted))
            {
                return;
            }

            progress.HighestAttempt = firedAt;
            await this.store.RecordFiredAsync(expected.Name, firedAt, nextFireAt, CancellationToken.None).ConfigureAwait(false);
            var updated = current with { LastFiredAt = firedAt, NextFireAt = nextFireAt };
            this.checkpointOrigins.Add(updated, this.CheckpointOrigin(current));
            this.jobs.TryUpdate(expected.Name, updated, current);
        }
    }

    private sealed class CheckpointProgress
    {
        internal DateTimeOffset? HighestAttempt { get; set; }
    }

    private JobRecord CheckpointOrigin(JobRecord record)
        => this.checkpointOrigins.TryGetValue(record, out var origin) ? origin : record;

    private async ValueTask MutateAsync(string name, Func<ValueTask> action, CancellationToken ct)
    {
        this.ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(name);
        using var lease = await this.mutationGates.EnterAsync(name, ct).ConfigureAwait(false);
        await action().ConfigureAwait(false);
    }

    private async ValueTask RegisterCoreAsync(
        string name,
        Cadence cadence,
        MissedFirePolicy missedFirePolicy,
        IJobDispatcher dispatcher,
        CancellationToken ct = default)
    {
        this.ThrowIfDisposed();
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(cadence);
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (this.jobs.ContainsKey(name))
        {
            throw new DuplicateJobNameException(name);
        }

        var now = this.timeProvider.GetUtcNow();

        // Resolve a relative one-shot to an absolute instant using the registry's
        // injected TimeProvider (R1/DR-2): the static factory Cadence.After performs
        // no clock access; resolution happens here, once, at registration time.
        cadence = cadence is RelativeOneShotCadence relative
            ? new OneShotCadence(now + relative.Delay)
            : cadence;

        // Reject a one-shot whose fire instant is in the past: a past one-shot would
        // fire immediately or be silently swallowed by the missed-fire policy, both of
        // which are ergonomics traps (quartznet#636/#2180, Hangfire#1637, R10).
        // "Past" is judged against the registry's injected TimeProvider (DR-7).
        if (cadence is OneShotCadence absoluteOneShot
            && absoluteOneShot.FireAt < now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cadence),
                $"A one-shot cadence with FireAt '{absoluteOneShot.FireAt:O}' is in the past " +
                $"(now: '{now:O}'). Register a future instant to " +
                "avoid a silent immediate fire or a missed-fire-policy decision at registration.");
        }

        var nextFireAt = cadence.ComputeNextFire(lastFiredAt: null, now: now);
        var record = new JobRecord(
            Name: name,
            Cadence: cadence,
            MissedFirePolicy: missedFirePolicy,
            State: JobState.Running,
            LastFiredAt: null,
            NextFireAt: nextFireAt,
            DispatchKind: DeriveDispatchKind(dispatcher),
            DispatcherTypeName: dispatcher.GetType().FullName,
            Metadata: new Dictionary<string, string>());

        // Claim the in-memory slot FIRST, then persist. Ordering matters under a
        // concurrent same-name race: SaveAsync is a blind upsert, so if we persisted
        // first a losing registrant would have already written its (possibly different)
        // payload to the store before TryAdd rejected it — leaving the store holding
        // loser data while the registry holds the winner. Claiming the slot first means
        // a loser is rejected before it ever touches the store. We then persist; if the
        // store write fails we compensate by releasing the slot so a store failure never
        // leaves the job in the registry (the existing rollback invariant).
        if (!this.jobs.TryAdd(name, record))
        {
            throw new DuplicateJobNameException(name);
        }

        try
        {
            await this.store.SaveAsync(record, ct).ConfigureAwait(false);
        }
        catch
        {
            // Compensate: the slot we claimed must not outlive a failed persist.
            this.jobs.TryRemove(name, out _);
            throw;
        }

        this.BindDispatcher(name, dispatcher);
        this.metrics.RecordRegistered();
        this.events.Publish(new JobRegisteredEvent(name));
        await this.PostAsync(new RegistryCommand(RegistryCommandKind.Register, name), CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask<bool> UnregisterCoreAsync(string name, CancellationToken ct = default)
    {
        this.ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(name);

        if (!this.jobs.ContainsKey(name))
        {
            return false;
        }

        await this.store.DeleteAsync(name, ct).ConfigureAwait(false);

        this.jobs.TryRemove(name, out _);
        this.UnbindDispatcher(name);
        this.metrics.RecordUnregistered();
        this.events.Publish(new JobUnregisteredEvent(name));

        // Post-commit wake uses CancellationToken.None: once durable + in-memory state
        // is committed, a caller cancellation must NOT drop the tick-loop wake, or the
        // loop would keep firing a job the registry has already removed (DR-1).
        await this.PostAsync(new RegistryCommand(RegistryCommandKind.Unregister, name), CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private async ValueTask PauseCoreAsync(string name, CancellationToken ct = default)
    {
        this.ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(name);

        if (!this.jobs.TryGetValue(name, out var record))
        {
            throw new JobNotFoundException(name);
        }

        // Already paused: nothing to persist or signal.
        if (record.State == JobState.Paused)
        {
            return;
        }

        await this.TransitionAsync(record, JobState.Paused, RegistryCommandKind.Pause, ct).ConfigureAwait(false);
    }

    private async ValueTask ResumeCoreAsync(string name, CancellationToken ct = default)
    {
        this.ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(name);

        if (!this.jobs.TryGetValue(name, out var record))
        {
            throw new JobNotFoundException(name);
        }

        // Already running: nothing to persist or signal.
        if (record.State == JobState.Running)
        {
            return;
        }

        await this.TransitionAsync(record, JobState.Running, RegistryCommandKind.Resume, ct).ConfigureAwait(false);
    }

    private async ValueTask UpdateCoreAsync(
        string name,
        Cadence cadence,
        MissedFirePolicy missedFirePolicy,
        CancellationToken ct = default)
    {
        this.ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(cadence);

        if (!this.jobs.TryGetValue(name, out var record))
        {
            throw new JobNotFoundException(name);
        }

        var now = this.timeProvider.GetUtcNow();

        // Apply the same resolution and validation as registration: relative one-shots
        // are resolved against the registry clock; past one-shots are rejected (R10).
        cadence = cadence is RelativeOneShotCadence relative
            ? new OneShotCadence(now + relative.Delay)
            : cadence;

        if (cadence is OneShotCadence absoluteOneShot
            && absoluteOneShot.FireAt < now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cadence),
                $"A one-shot cadence with FireAt '{absoluteOneShot.FireAt:O}' is in the past " +
                $"(now: '{now:O}'). Updating to a past instant would " +
                "cause a silent immediate fire or a missed-fire-policy decision.");
        }

        // Recompute NextFireAt from the new cadence. Mutating operations must never
        // cause immediate execution as a side effect (R10); TriggerAsync is the only
        // fire-on-demand API.
        var nextFireAt = cadence.ComputeNextFire(lastFiredAt: null, now: now);
        var updated = record with
        {
            Cadence = cadence,
            MissedFirePolicy = missedFirePolicy,
            NextFireAt = nextFireAt,
        };

        await this.store.SaveAsync(updated, ct).ConfigureAwait(false);

        this.jobs[name] = updated;
        // Post a Register command so the tick loop re-arms the job from the updated
        // record immediately, computing the new NextFireAt without any immediate fire
        // side effect. The Register command calls Arm which reads the updated registry.
        // CancellationToken.None: the durable + in-memory update is already committed, so
        // a caller cancellation must not drop the re-arm wake (DR-1).
        await this.PostAsync(new RegistryCommand(RegistryCommandKind.Register, name), CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask TriggerAsync(string name, CancellationToken ct = default)
    {
        this.ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(name);

        if (!this.jobs.ContainsKey(name))
        {
            throw new JobNotFoundException(name);
        }

        // Trigger does not commit any durable or in-memory state before posting: the
        // post IS the operation. Unlike the post-commit wakes (Register/Unregister/
        // Transition/Update, which use CancellationToken.None so a late cancel cannot
        // orphan already-committed state), there is nothing committed here to protect,
        // so a caller cancellation should cleanly abort the trigger via OCE and enqueue
        // nothing — matching the caller-cancel→OCE contract for uncommitted operations.
        // The admission slot stays held until the tick loop hands the trigger off,
        // drops it, or abandons it at shutdown. A running dispatch must not wait:
        // the slot it needs is released only after that dispatch finishes.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, this.triggerAdmissionCancelled.Token);
        var acquired = false;
        try
        {
            if (DispatchAdmission.IsReentrant)
            {
                if (!await this.triggerAdmission.WaitAsync(TimeSpan.Zero, linked.Token).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "TriggerAsync cannot wait for an admission slot while a dispatch is running.");
                }
            }
            else
            {
                await this.triggerAdmission.WaitAsync(linked.Token).ConfigureAwait(false);
            }

            acquired = true;
            var registrationId = this.RegistrationId(name);
            await this.PostAsync(new RegistryCommand(RegistryCommandKind.Trigger, name, registrationId), linked.Token).ConfigureAwait(false);
            acquired = false;
        }
        finally
        {
            if (acquired)
            {
                this.triggerAdmission.Release();
            }
        }
    }

    /// <summary>
    /// Returns one trigger admission slot after the tick loop hands the trigger
    /// off, drops it, or abandons it.
    /// </summary>
    internal void ReleaseTriggerAdmission() => this.triggerAdmission.Release();

    /// <inheritdoc/>
    public IReadOnlyList<JobDescriptor> GetJobs()
    {
        var result = new List<JobDescriptor>(this.jobs.Count);
        foreach (var record in this.jobs.Values)
        {
            result.Add(ToDescriptor(record));
        }

        result.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <inheritdoc/>
    public JobDescriptor? GetJob(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return this.jobs.TryGetValue(name, out var record) ? ToDescriptor(record) : null;
    }

    /// <summary>
    /// Projects a persisted <see cref="JobRecord"/> to the read-only
    /// <see cref="JobDescriptor"/> returned by the inspection APIs. <c>IsRunning</c>
    /// reflects the job's scheduling state (<see cref="JobState.Running"/>); a live
    /// dispatch-in-flight signal is wired in a later group.
    /// </summary>
    /// <param name="record">The persisted record to project.</param>
    /// <returns>The descriptor snapshot.</returns>
    private static JobDescriptor ToDescriptor(JobRecord record)
        => new(
            Name: record.Name,
            State: record.State,
            Cadence: record.Cadence,
            LastFiredAt: record.LastFiredAt,
            NextFireAt: record.NextFireAt,
            IsRunning: record.State == JobState.Running);

    /// <summary>
    /// Derives the persisted <see cref="JobRecord.DispatchKind"/> tag for a
    /// dispatcher. This group defaults to <c>"custom"</c>; later groups refine the
    /// mapping for the orchestrator and inline dispatchers.
    /// </summary>
    /// <param name="dispatcher">The dispatcher being registered.</param>
    /// <returns>The dispatch-kind tag.</returns>
    private static string DeriveDispatchKind(IJobDispatcher dispatcher) => "custom";

    /// <summary>
    /// Transitions a job to a new lifecycle state: persists the updated record,
    /// updates the in-memory metadata, and posts the corresponding wake command. The
    /// store write precedes the in-memory update so a persistence failure leaves the
    /// registry and store consistent (DR-1).
    /// </summary>
    /// <param name="record">The current job record being transitioned.</param>
    /// <param name="state">The state to transition to.</param>
    /// <param name="kind">The wake command to post after the transition.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A task that completes when the transition is persisted and signalled.</returns>
    private async ValueTask TransitionAsync(
        JobRecord record,
        JobState state,
        RegistryCommandKind kind,
        CancellationToken ct)
    {
        var updated = record with { State = state };

        await this.store.SaveAsync(updated, ct).ConfigureAwait(false);

        this.jobs[updated.Name] = updated;
        this.PublishTransition(kind, updated.Name);

        // Post-commit wake uses CancellationToken.None (covers Pause and Resume): the
        // durable + in-memory transition is already committed, so a caller cancellation
        // must not drop the wake that tells the tick loop to honour the new state (DR-1).
        await this.PostAsync(new RegistryCommand(kind, updated.Name), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes the lifecycle event corresponding to a pause or resume transition.
    /// </summary>
    /// <param name="kind">The transition command kind.</param>
    /// <param name="name">The job that transitioned.</param>
    private void PublishTransition(RegistryCommandKind kind, string name)
    {
        switch (kind)
        {
            case RegistryCommandKind.Pause:
                this.events.Publish(new JobPausedEvent(name));
                break;

            case RegistryCommandKind.Resume:
                this.events.Publish(new JobResumedEvent(name));
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Validates a job name against the lowercase identity pattern, throwing when it
    /// is null or does not match.
    /// </summary>
    /// <param name="name">The candidate job name.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is null, empty, or does not match the pattern
    /// <c>^[a-z0-9][a-z0-9-_.]{0,127}$</c>.
    /// </exception>
    private static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!JobNameRegex().IsMatch(name))
        {
            throw new ArgumentException(
                $"Job name '{name}' is invalid: names must match the pattern " +
                "^[a-z0-9][a-z0-9-_.]{0,127}$ (lowercase, starting with a letter or " +
                "digit, at most 128 characters).",
                nameof(name));
        }
    }

    /// <summary>
    /// The compiled job-name pattern. Source-generated for AOT compatibility
    /// (no runtime regex codegen).
    /// </summary>
    /// <returns>The compiled job-name regex.</returns>
    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-_.]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex JobNameRegex();

    private async ValueTask PostAsync(RegistryCommand command, CancellationToken ct)
        => await this.commands.Writer.WriteAsync(command, ct).ConfigureAwait(false);

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> when the registry has been disposed.
    /// Called at the start of every public mutating / command-posting method — before any
    /// durable or in-memory mutation — so a disposed registry rejects the call cleanly
    /// rather than committing the change and only failing later at the closed-channel
    /// <see cref="PostAsync"/>, which would leave the caller seeing a committed change
    /// reported as failed. Read-only query accessors do not call this: a snapshot of a
    /// disposed registry is harmless.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The registry has been disposed.</exception>
    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);

    /// <summary>
    /// Disposes the registry by completing its command-channel writer, signalling the
    /// tick loop's reader that no further wake commands will arrive. The command channel
    /// is single-reader, so once the writer completes no further command can arrive: the
    /// loop drains the remaining backlog, observes the completion as a clean-stop signal,
    /// and exits its tick loop instead of blocking forever on
    /// <see cref="ChannelReader{T}.WaitToReadAsync"/> over a writer that is never
    /// completed — or re-parking on an already-completed channel and hot-spinning the CPU.
    /// Idempotent: a second call (sync or async) is a no-op (a single-shot
    /// <see cref="Interlocked"/> guard ensures the writer is completed exactly once,
    /// since completing an already-completed channel would otherwise throw).
    /// </summary>
    /// <remarks>
    /// The registry implements both <see cref="IAsyncDisposable"/> and
    /// <see cref="IDisposable"/> so it tears down cleanly under either disposal path:
    /// completing the writer is a synchronous, non-blocking operation, so the async path
    /// returns a completed <see cref="ValueTask"/> and shares this same logic. Both are
    /// provided deliberately — a DI container disposed synchronously
    /// (<c>ServiceProvider.Dispose()</c>) throws if a resolved service implements
    /// <see cref="IAsyncDisposable"/> but not <see cref="IDisposable"/>, and a consumer
    /// calling only <c>Dispose</c> on an async-only type would never complete the writer
    /// (leaking the reader). Completing the writer does not discard queued commands: any
    /// already-posted command remains readable, and the reader observes completion only
    /// after it has drained them.
    /// </remarks>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
    public ValueTask DisposeAsync()
    {
        this.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Disposes the registry by completing its command-channel writer so the tick loop's
    /// reader drains and exits. See <see cref="DisposeAsync"/> for the full contract;
    /// this synchronous path exists so a synchronously-disposed DI container or a
    /// <c>using</c> consumer tears the registry down correctly. Idempotent across both
    /// the sync and async paths via a shared single-shot <see cref="Interlocked"/> guard.
    /// </summary>
    public void Dispose()
    {
        // Idempotent: only the first caller completes the writer; a second Complete()
        // would throw, so the guard makes double-dispose (sync or async) safe.
        if (Interlocked.Exchange(ref this.disposed, 1) == 0)
        {
            // Leave the semaphore and the cancellation source usable. The tick loop
            // can still release a slot for a command it drains after the writer completes.
            this.triggerAdmissionCancelled.Cancel();
            this.commands.Writer.Complete();
        }
    }
}
