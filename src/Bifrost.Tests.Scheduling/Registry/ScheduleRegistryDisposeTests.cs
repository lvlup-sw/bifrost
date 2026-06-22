// =============================================================================
// <copyright file="ScheduleRegistryDisposeTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Threading.Channels;

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.DependencyInjection;
using Bifrost.Scheduling.Registry;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

namespace Bifrost.Tests.Scheduling.Registry;

/// <summary>
/// Tests for <see cref="ScheduleRegistry"/> disposal (#32, G6c): disposing the
/// registry completes its command-channel writer so the tick loop's reader drains
/// and exits cleanly, disposal is idempotent, and a post after dispose surfaces an
/// observable error rather than an unobserved fault.
/// </summary>
public sealed class ScheduleRegistryDisposeTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies disposing the registry completes the command-channel writer: a reader
    /// blocked on <see cref="ChannelReader{T}.WaitToReadAsync"/> over an otherwise empty
    /// channel observes completion (the wait returns <see langword="false"/>) rather
    /// than hanging on a writer that is never completed — the exact tick-loop drain the
    /// dispose contract guarantees.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DisposeAsync_CompletesCommandWriter_PendingWaitObservesCompletion()
    {
        var store = Substitute.For<IScheduleStore>();
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));

        // Mirror the tick loop's wait: park on the (empty) command channel before dispose.
        var pendingWait = registry.Commands.WaitToReadAsync().AsTask();
        await Assert.That(pendingWait.IsCompleted).IsFalse();

        await registry.DisposeAsync().ConfigureAwait(false);

        // The writer is now completed, so the parked wait completes returning false —
        // the signal that lets the reader's drain loop end instead of blocking forever.
        var canRead = await pendingWait.ConfigureAwait(false);
        await Assert.That(canRead).IsFalse();
        await Assert.That(registry.Commands.Completion.IsCompletedSuccessfully).IsTrue();
    }

    /// <summary>
    /// Verifies a pending command posted before dispose is still readable after dispose:
    /// completing the writer drains the backlog rather than discarding it, and the reader
    /// observes completion only after that backlog is consumed.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DisposeAsync_AfterCommandPosted_DrainsBacklogThenCompletes()
    {
        var store = Substitute.For<IScheduleStore>();
        var registry = await RegisterJobAsync(store, "daily-report").ConfigureAwait(false);

        // RegisterAsync posted a Register command; dispose must not drop it.
        await registry.DisposeAsync().ConfigureAwait(false);

        var drained = DrainCommands(registry);
        await Assert.That(drained).Contains(
            c => c.Kind == RegistryCommandKind.Register && c.JobName == "daily-report");

        // Backlog consumed: the reader now observes completion (no further read).
        await Assert.That(registry.Commands.TryRead(out _)).IsFalse();
        await Assert.That(registry.Commands.Completion.IsCompletedSuccessfully).IsTrue();
    }

    /// <summary>
    /// Verifies disposing twice is safe: the single-shot guard means the second call
    /// does not re-complete the (already-completed) writer, so it does not throw.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DisposeAsync_CalledTwice_IsSafe()
    {
        var store = Substitute.For<IScheduleStore>();
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));

        await registry.DisposeAsync().ConfigureAwait(false);

        await Assert.That(async () => await registry.DisposeAsync().ConfigureAwait(false))
            .ThrowsNothing();
        await Assert.That(registry.Commands.Completion.IsCompletedSuccessfully).IsTrue();
    }

    /// <summary>
    /// Verifies a command-posting method after dispose fails fast with a clear
    /// <see cref="ObjectDisposedException"/> rather than the late
    /// <see cref="ChannelClosedException"/> the bare closed-channel post would surface.
    /// The dispose guard (see <see cref="MutatingMethods_AfterDispose_Throw"/>) runs before
    /// the post, so a caller using a disposed registry gets a clear, awaited error that names
    /// the disposed object instead of a leaked channel fault — and, critically, no state is
    /// committed before that failure.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task PostAfterDispose_FailsFastWithObjectDisposed()
    {
        var store = Substitute.For<IScheduleStore>();
        var registry = await RegisterJobAsync(store, "daily-report").ConfigureAwait(false);

        await registry.DisposeAsync().ConfigureAwait(false);

        // TriggerAsync would post unconditionally for a registered job; the dispose guard
        // now rejects it up front with ObjectDisposedException, before it reaches the
        // completed-writer post (which would otherwise throw ChannelClosedException).
        await Assert.That(async () => await registry.TriggerAsync("daily-report").ConfigureAwait(false))
            .Throws<ObjectDisposedException>();
    }

    /// <summary>
    /// Verifies the registry tears down cleanly when its DI container is disposed
    /// <em>synchronously</em>. The registry is a DI-managed service; a service that
    /// implements <see cref="IAsyncDisposable"/> but not <see cref="IDisposable"/> makes
    /// <c>ServiceProvider.Dispose()</c> throw, so the registry must implement both. This
    /// guards that regression: building a provider with the registry and disposing it via
    /// the synchronous <c>using</c> path must not throw.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task SynchronousContainerDispose_DoesNotThrow()
    {
        await Assert.That(() =>
        {
            var services = new ServiceCollection();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<StubDispatcher>();
            services.AddScheduler(b =>
                b.AddJob<object>("probe-job")
                 .Every(TimeSpan.FromHours(1))
                 .DispatchVia<StubDispatcher>());

            // Resolve the registry so the container owns a live instance, then dispose
            // the provider synchronously (the path ServiceProvider.Dispose() takes).
            using var sp = services.BuildServiceProvider();
            _ = sp.GetRequiredService<IScheduleRegistry>();
        }).ThrowsNothing();
    }

    /// <summary>
    /// Verifies that after the registry is disposed every public mutating / command-posting
    /// method fails fast with <see cref="ObjectDisposedException"/> — before it can commit a
    /// durable or in-memory change. Without the guard a mutation commits and only the trailing
    /// closed-channel post fails, so the caller sees a committed change reported as failed.
    /// <c>RegisterAsync</c> (no prior job) and <c>PauseAsync</c>/<c>UpdateAsync</c>/
    /// <c>ResumeAsync</c>/<c>UnregisterAsync</c>/<c>TriggerAsync</c> (a previously-registered
    /// job) all reject the call rather than committing.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task MutatingMethods_AfterDispose_Throw()
    {
        var store = Substitute.For<IScheduleStore>();
        var registry = await RegisterJobAsync(store, "daily-report").ConfigureAwait(false);

        await registry.DisposeAsync().ConfigureAwait(false);

        // Forget the SaveAsync the setup registration recorded so the no-store assertion
        // below scopes only to the calls made AFTER dispose.
        store.ClearReceivedCalls();

        // RegisterAsync of a NEW job rejects before claiming the name slot or persisting.
        await Assert.That(async () => await registry.RegisterAsync(
                "new-job", new IntervalCadence(TimeSpan.FromHours(1)), MissedFirePolicy.Coalesce, new StubDispatcher())
            .ConfigureAwait(false))
            .Throws<ObjectDisposedException>();

        // Mutations over the already-registered job reject before they touch the store.
        await Assert.That(async () => await registry.PauseAsync("daily-report").ConfigureAwait(false))
            .Throws<ObjectDisposedException>();
        await Assert.That(async () => await registry.ResumeAsync("daily-report").ConfigureAwait(false))
            .Throws<ObjectDisposedException>();
        await Assert.That(async () => await registry.UpdateAsync(
                "daily-report", new IntervalCadence(TimeSpan.FromMinutes(30)), MissedFirePolicy.Coalesce)
            .ConfigureAwait(false))
            .Throws<ObjectDisposedException>();
        await Assert.That(async () => await registry.UnregisterAsync("daily-report").ConfigureAwait(false))
            .Throws<ObjectDisposedException>();
        await Assert.That(async () => await registry.TriggerAsync("daily-report").ConfigureAwait(false))
            .Throws<ObjectDisposedException>();

        // The guard runs before any durable mutation: the store was never written through
        // (no Save/Delete) by any of the rejected calls after dispose.
        await store.DidNotReceive().SaveAsync(Arg.Any<JobRecord>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await store.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
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
