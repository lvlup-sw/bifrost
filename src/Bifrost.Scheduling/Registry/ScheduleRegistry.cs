// =============================================================================
// <copyright file="ScheduleRegistry.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using Bifrost.Scheduling.Core;

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
    public ScheduleRegistry(IScheduleStore store, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.store = store;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Gets the reader over the wake channel: the tick loop drains this to learn of
    /// registration, removal, pause, resume, and trigger actions as they happen.
    /// </summary>
    internal ChannelReader<RegistryCommand> Commands => this.commands.Reader;

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
        await this.PostAsync(new RegistryCommand(kind, updated.Name), ct).ConfigureAwait(false);
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
