// =============================================================================
// <copyright file="RegistryCorrectnessTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;
using Bifrost.Scheduling.Registry;

using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.Registry;

/// <summary>
/// Structural and concurrency correctness tests for <see cref="ScheduleRegistry"/>:
/// minted registration identities on all commands, sentinel rejection, and atomic
/// disposal vs. store-await commit guards.
/// </summary>
public sealed class RegistryCorrectnessTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies that non-positive sentinel registration IDs are rejected by
    /// <see cref="ScheduleRegistry.ResolveDispatcher"/> and
    /// <see cref="ScheduleRegistry.TryMarkJobFaulted"/> rather than treated as wildcards.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task SentinelZeroIds_AreRejectedByChokepoints()
    {
        var store = new GatingStore();
        store.ReleaseAll();
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));
        await registry.RegisterAsync(
                "job",
                Cadence.Interval(TimeSpan.FromMinutes(5)),
                MissedFirePolicy.Coalesce,
                new StubDispatcher())
            .ConfigureAwait(false);

        await Assert.That(() => registry.ResolveDispatcher("job", 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => registry.ResolveDispatcher("job", -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => registry.TryMarkJobFaulted(0, "job")).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => registry.TryMarkJobFaulted(-1, "job")).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Verifies that every lifecycle command (<c>Register</c>, <c>Pause</c>, <c>Resume</c>,
    /// <c>Trigger</c>, <c>Unregister</c>) carries the registration's positive identity
    /// rather than leaving <see cref="RegistryCommand.RegistrationId"/> as <c>0</c>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task LifecycleCommands_CarryPositiveRegistrationId()
    {
        var store = new GatingStore();
        store.ReleaseAll();
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));

        await registry.RegisterAsync(
                "job",
                Cadence.Interval(TimeSpan.FromMinutes(5)),
                MissedFirePolicy.Coalesce,
                new StubDispatcher())
            .ConfigureAwait(false);
        var id = registry.RegistrationId("job");
        await Assert.That(id).IsGreaterThan(0L);

        await registry.PauseAsync("job").ConfigureAwait(false);
        await registry.ResumeAsync("job").ConfigureAwait(false);
        await registry.TriggerAsync("job").ConfigureAwait(false);
        await registry.UnregisterAsync("job").ConfigureAwait(false);

        var commands = new List<RegistryCommand>();
        while (registry.Commands.TryRead(out var cmd))
        {
            commands.Add(cmd);
        }

        await Assert.That(commands).HasCount(5);
        foreach (var cmd in commands)
        {
            await Assert.That(cmd.RegistrationId).IsEqualTo(id);
        }
    }

    /// <summary>
    /// Verifies that replacing a job under the same name mints a new registration ID,
    /// so <see cref="ScheduleRegistry.TryMarkJobFaulted"/> carrying the old ID cannot
    /// fault the replacement registration.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ReplacedRegistration_OldIdentityCannotFaultReplacement()
    {
        var store = new GatingStore();
        store.ReleaseAll();
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));

        await registry.RegisterAsync(
                "job",
                Cadence.Interval(TimeSpan.FromMinutes(5)),
                MissedFirePolicy.Coalesce,
                new StubDispatcher())
            .ConfigureAwait(false);
        var id1 = registry.RegistrationId("job");

        await registry.UnregisterAsync("job").ConfigureAwait(false);
        await registry.RegisterAsync(
                "job",
                Cadence.Interval(TimeSpan.FromMinutes(10)),
                MissedFirePolicy.Coalesce,
                new StubDispatcher())
            .ConfigureAwait(false);
        var id2 = registry.RegistrationId("job");

        await Assert.That(id2).IsNotEqualTo(id1);
        await Assert.That(registry.TryMarkJobFaulted(id1, "job")).IsFalse();
        await Assert.That(registry.GetJob("job")!.Value.State).IsEqualTo(JobState.Running);
    }

    /// <summary>
    /// Verifies that if <see cref="ScheduleRegistry.DisposeAsync"/> runs while
    /// <see cref="ScheduleRegistry.RegisterAsync"/> is awaiting the store, the in-flight
    /// registration fails cleanly with <see cref="ObjectDisposedException"/>, leaves no
    /// half-registered job in the registry, and emits no lifecycle event.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DisposeDuringSaveAwait_RegisterThrowsObjectDisposed_LeavesNoEntryOrEvent()
    {
        var store = new GatingStore();
        var events = new RecordingSink();
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now), events: events);

        var registerTask = registry.RegisterAsync(
                "job",
                Cadence.Interval(TimeSpan.FromMinutes(5)),
                MissedFirePolicy.Coalesce,
                new StubDispatcher())
            .AsTask();

        await store.WaitForSaveEnteredAsync().ConfigureAwait(false);
        await registry.DisposeAsync().ConfigureAwait(false);
        store.ReleaseNextSave();

        try
        {
            await registerTask.ConfigureAwait(false);
            Assert.Fail("Expected ObjectDisposedException.");
        }
        catch (ObjectDisposedException)
        {
            // Expected: in-flight registration failed cleanly on disposal.
        }

        await Assert.That(registry.GetJob("job")).IsNull();
        await Assert.That(events.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that if <see cref="ScheduleRegistry.DisposeAsync"/> runs while
    /// <see cref="ScheduleRegistry.PauseAsync"/> is awaiting the store, the in-flight
    /// pause fails cleanly with <see cref="ObjectDisposedException"/> without mutating
    /// the in-memory job state or publishing <see cref="JobPausedEvent"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DisposeDuringSaveAwait_PauseThrowsObjectDisposed_DoesNotMutateOrPublish()
    {
        var store = new GatingStore();
        store.ReleaseNextSave();
        var events = new RecordingSink();
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now), events: events);

        await registry.RegisterAsync(
                "job",
                Cadence.Interval(TimeSpan.FromMinutes(5)),
                MissedFirePolicy.Coalesce,
                new StubDispatcher())
            .ConfigureAwait(false);
        events.Clear();

        var pauseTask = registry.PauseAsync("job").AsTask();
        await store.WaitForSaveEnteredAsync().ConfigureAwait(false);
        await registry.DisposeAsync().ConfigureAwait(false);
        store.ReleaseNextSave();

        try
        {
            await pauseTask.ConfigureAwait(false);
            Assert.Fail("Expected ObjectDisposedException.");
        }
        catch (ObjectDisposedException)
        {
            // Expected: in-flight pause failed cleanly on disposal.
        }

        await Assert.That(registry.GetJob("job")!.Value.State).IsEqualTo(JobState.Running);
        await Assert.That(events.Count).IsEqualTo(0);
    }

    private sealed class StubDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class RecordingSink : ISchedulerEventSink
    {
        private int count;

        public int Count => Volatile.Read(ref this.count);

        public void Publish<TEvent>(in TEvent schedulerEvent)
            where TEvent : struct => Interlocked.Increment(ref this.count);

        public void Clear() => Interlocked.Exchange(ref this.count, 0);
    }

    private sealed class GatingStore : IScheduleStore
    {
        private readonly ConcurrentDictionary<string, JobRecord> jobs = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim saveEntered = new(0);
        private readonly SemaphoreSlim saveGate = new(0);
        private readonly object gateLock = new();
        private bool releaseAll;

        public async ValueTask SaveAsync(JobRecord job, CancellationToken ct)
        {
            bool autoRelease;
            lock (this.gateLock)
            {
                autoRelease = this.releaseAll;
            }

            this.saveEntered.Release();
            if (!autoRelease)
            {
                await this.saveGate.WaitAsync(ct).ConfigureAwait(false);
            }

            this.jobs[job.Name] = job;
        }

        public ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<JobRecord>>([.. this.jobs.Values]);

        public ValueTask RecordFiredAsync(
            string jobName, DateTimeOffset firedAt, DateTimeOffset? nextFireAt, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask DeleteAsync(string jobName, CancellationToken ct)
        {
            this.jobs.TryRemove(jobName, out _);
            return ValueTask.CompletedTask;
        }

        public async Task WaitForSaveEnteredAsync()
            => await this.saveEntered.WaitAsync().ConfigureAwait(false);

        public void ReleaseNextSave() => this.saveGate.Release();

        public void ReleaseAll()
        {
            lock (this.gateLock)
            {
                this.releaseAll = true;
            }
        }
    }
}
