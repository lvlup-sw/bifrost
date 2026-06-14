// =============================================================================
// <copyright file="ScheduleRegistry.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;
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
/// Durable state flows through the store: registration, pause, resume, and removal
/// write through before the in-memory state is mutated, so a store failure rolls
/// back the change rather than leaving the registry and store divergent. Names are
/// the identity key and are validated against a compiled lowercase pattern.
/// </remarks>
public sealed partial class ScheduleRegistry : IScheduleRegistry
{
    private readonly IScheduleStore store;
    private readonly TimeProvider timeProvider;
    private readonly SchedulerMetrics metrics;
    private readonly ISchedulerEventSink events;
    private readonly ConcurrentDictionary<string, JobRecord> jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IJobDispatcher> dispatchers = new(StringComparer.Ordinal);
    private readonly Channel<RegistryCommand> commands =
        Channel.CreateUnbounded<RegistryCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

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
    /// Enumerates a snapshot of every currently registered job's record. The tick
    /// loop uses this when seeding its heap at startup to reconcile store-loaded jobs
    /// against the live, dispatcher-bearing registry. This is an internal read
    /// accessor.
    /// </summary>
    /// <returns>A snapshot of all registered job records.</returns>
    internal IReadOnlyList<JobRecord> SnapshotRecords() => [.. this.jobs.Values];

    /// <inheritdoc/>
    public async ValueTask RegisterAsync(
        string name,
        Cadence cadence,
        MissedFirePolicy missedFirePolicy,
        IJobDispatcher dispatcher,
        CancellationToken ct = default)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(cadence);
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (this.jobs.ContainsKey(name))
        {
            throw new DuplicateJobNameException(name);
        }

        var nextFireAt = cadence.ComputeNextFire(lastFiredAt: null, now: this.timeProvider.GetUtcNow());
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

        // Persist first: a store failure must not leave the job in the registry.
        await this.store.SaveAsync(record, ct).ConfigureAwait(false);

        // A concurrent registration of the same name may have won the race; if so,
        // the persisted record is harmless (it would be overwritten) but we must not
        // double-register in memory.
        if (!this.jobs.TryAdd(name, record))
        {
            throw new DuplicateJobNameException(name);
        }

        this.dispatchers[name] = dispatcher;
        this.metrics.RecordRegistered();
        this.events.Publish(new JobRegisteredEvent(name));
        await this.PostAsync(new RegistryCommand(RegistryCommandKind.Register, name), ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<bool> UnregisterAsync(string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!this.jobs.ContainsKey(name))
        {
            return false;
        }

        await this.store.DeleteAsync(name, ct).ConfigureAwait(false);

        this.jobs.TryRemove(name, out _);
        this.dispatchers.TryRemove(name, out _);
        this.metrics.RecordUnregistered();
        this.events.Publish(new JobUnregisteredEvent(name));
        await this.PostAsync(new RegistryCommand(RegistryCommandKind.Unregister, name), ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async ValueTask PauseAsync(string name, CancellationToken ct = default)
    {
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

    /// <inheritdoc/>
    public async ValueTask ResumeAsync(string name, CancellationToken ct = default)
    {
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

    /// <inheritdoc/>
    public async ValueTask TriggerAsync(string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!this.jobs.ContainsKey(name))
        {
            throw new JobNotFoundException(name);
        }

        // Trigger does not itself dispatch or mutate state: it posts a command and
        // the tick loop performs the out-of-band fire.
        await this.PostAsync(new RegistryCommand(RegistryCommandKind.Trigger, name), ct).ConfigureAwait(false);
    }

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
        await this.PostAsync(new RegistryCommand(kind, updated.Name), ct).ConfigureAwait(false);
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
    [GeneratedRegex("^[a-z0-9][a-z0-9-_.]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex JobNameRegex();

    private async ValueTask PostAsync(RegistryCommand command, CancellationToken ct)
        => await this.commands.Writer.WriteAsync(command, ct).ConfigureAwait(false);
}
