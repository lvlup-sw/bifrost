// =============================================================================
// <copyright file="ScheduleRegistryControlTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Registry;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

namespace Bifrost.Tests.Scheduling.Registry;

/// <summary>
/// Tests for <see cref="ScheduleRegistry"/> pause, resume, and trigger (Task 20,
/// DR-1): state transitions with persistence, no-op idempotency, missing-job
/// errors, and the trigger command posted to the wake channel.
/// </summary>
public sealed class ScheduleRegistryControlTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies pausing a running job transitions it to paused and persists.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Pause_Running_TransitionsToPausedAndPersists()
    {
        var store = Substitute.For<IScheduleStore>();
        var registry = await RegisterJobAsync(store, "daily-report").ConfigureAwait(false);
        store.ClearReceivedCalls();

        await registry.PauseAsync("daily-report").ConfigureAwait(false);

        await Assert.That(registry.GetJob("daily-report")!.Value.State).IsEqualTo(JobState.Paused);
        await store.Received(1).SaveAsync(
            Arg.Is<JobRecord>(j => j.State == JobState.Paused),
            Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies pausing an already-paused job is a no-op (no throw).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Pause_AlreadyPaused_IsNoOp()
    {
        var store = Substitute.For<IScheduleStore>();
        var registry = await RegisterJobAsync(store, "daily-report").ConfigureAwait(false);
        await registry.PauseAsync("daily-report").ConfigureAwait(false);

        await registry.PauseAsync("daily-report").ConfigureAwait(false);

        await Assert.That(registry.GetJob("daily-report")!.Value.State).IsEqualTo(JobState.Paused);
    }

    /// <summary>
    /// Verifies pausing a non-existent job throws <see cref="JobNotFoundException"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Pause_NonExistent_Throws()
    {
        var store = Substitute.For<IScheduleStore>();
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));

        await Assert.That(async () => await registry.PauseAsync("missing").ConfigureAwait(false))
            .Throws<JobNotFoundException>();
    }

    /// <summary>
    /// Verifies resuming a paused job transitions it to running and persists.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Resume_Paused_TransitionsToRunningAndPersists()
    {
        var store = Substitute.For<IScheduleStore>();
        var registry = await RegisterJobAsync(store, "daily-report").ConfigureAwait(false);
        await registry.PauseAsync("daily-report").ConfigureAwait(false);
        store.ClearReceivedCalls();

        await registry.ResumeAsync("daily-report").ConfigureAwait(false);

        await Assert.That(registry.GetJob("daily-report")!.Value.State).IsEqualTo(JobState.Running);
        await store.Received(1).SaveAsync(
            Arg.Is<JobRecord>(j => j.State == JobState.Running),
            Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies resuming an already-running job is a no-op (no throw).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Resume_AlreadyRunning_IsNoOp()
    {
        var store = Substitute.For<IScheduleStore>();
        var registry = await RegisterJobAsync(store, "daily-report").ConfigureAwait(false);

        await registry.ResumeAsync("daily-report").ConfigureAwait(false);

        await Assert.That(registry.GetJob("daily-report")!.Value.State).IsEqualTo(JobState.Running);
    }

    /// <summary>
    /// Verifies resuming a non-existent job throws <see cref="JobNotFoundException"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Resume_NonExistent_Throws()
    {
        var store = Substitute.For<IScheduleStore>();
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));

        await Assert.That(async () => await registry.ResumeAsync("missing").ConfigureAwait(false))
            .Throws<JobNotFoundException>();
    }

    /// <summary>
    /// Verifies triggering an existing job posts a <see cref="RegistryCommandKind.Trigger"/>
    /// command to the wake channel.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Trigger_Existing_PostsTriggerCommand()
    {
        var store = Substitute.For<IScheduleStore>();
        var registry = await RegisterJobAsync(store, "daily-report").ConfigureAwait(false);

        await registry.TriggerAsync("daily-report").ConfigureAwait(false);

        var commands = DrainCommands(registry);
        await Assert.That(commands).Contains(
            c => c.Kind == RegistryCommandKind.Trigger && c.JobName == "daily-report");
    }

    /// <summary>
    /// Verifies triggering a non-existent job throws <see cref="JobNotFoundException"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Trigger_NonExistent_Throws()
    {
        var store = Substitute.For<IScheduleStore>();
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));

        await Assert.That(async () => await registry.TriggerAsync("missing").ConfigureAwait(false))
            .Throws<JobNotFoundException>();
    }

    private static async Task<ScheduleRegistry> RegisterJobAsync(IScheduleStore store, string name)
    {
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));
        await registry.RegisterAsync(
                name, new IntervalCadence(TimeSpan.FromHours(1)), MissedFirePolicy.Coalesce, new StubDispatcher())
            .ConfigureAwait(false);
        return registry;
    }

    private static List<RegistryCommand> DrainCommands(ScheduleRegistry registry)
    {
        var commands = new List<RegistryCommand>();
        while (registry.Commands.TryRead(out var command))
        {
            commands.Add(command);
        }

        return commands;
    }

    private sealed class StubDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
