// =============================================================================
// <copyright file="ISchedulerTestHarness.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Testing;

/// <summary>
/// A testing-only control surface that lets tests drive the scheduler's tick loop
/// deterministically without any real wall-clock waits (DR-9). All three
/// primitives operate against the <see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider"/>
/// that was substituted into the DI container by
/// <c>AddSchedulerTesting</c>.
/// </summary>
/// <remarks>
/// <para>
/// The typical pattern in a scheduler test is:
/// <list type="number">
///   <item><description>Register a job through <see cref="Bifrost.Scheduling.Core.IScheduleRegistry"/>.</description></item>
///   <item><description>Call <see cref="AdvanceAsync"/> to move the fake clock forward.</description></item>
///   <item><description>Assert on the observable side-effects of the fires that occurred.</description></item>
/// </list>
/// </para>
/// <para>
/// Use <see cref="FireDueJobsAsync"/> when you want to fire every job that is
/// already due at the current fake-clock instant without advancing time further.
/// </para>
/// <para>
/// Use <see cref="WaitForIdleAsync"/> when you need to synchronize with the tick
/// loop's quiescent state — for example, after registering a job or issuing a
/// trigger command — without advancing time.
/// </para>
/// </remarks>
public interface ISchedulerTestHarness
{
    /// <summary>
    /// Advances the fake clock by <paramref name="duration"/>, fires every job
    /// that becomes due as a result, and waits until the tick loop is fully idle
    /// (all commands drained, all due jobs dispatched, no in-flight dispatch).
    /// </summary>
    /// <remarks>
    /// A single call to <see cref="AdvanceAsync"/> is deterministic: after it
    /// returns, every job whose scheduled occurrence falls within the advanced
    /// window has been dispatched to completion before the method returns.
    /// </remarks>
    /// <param name="duration">
    /// The amount of time to advance the fake clock. Must be a non-negative
    /// value; passing zero synchronizes with the current idle state without
    /// moving time.
    /// </param>
    /// <returns>A task that completes when the loop has reached its next idle point.</returns>
    Task AdvanceAsync(TimeSpan duration);

    /// <summary>
    /// Fires every job that is currently due at the present fake-clock instant
    /// and waits until the tick loop is fully idle. Does not advance the clock.
    /// </summary>
    /// <remarks>
    /// Useful for testing the outcome of a manual trigger or for asserting that
    /// no previously-due jobs were silently skipped.
    /// </remarks>
    /// <returns>A task that completes when the loop has reached its next idle point.</returns>
    Task FireDueJobsAsync();

    /// <summary>
    /// Waits until the tick loop reaches a fully quiescent state: all pending
    /// commands drained, all due jobs dispatched, and no in-flight dispatch.
    /// Throws <see cref="TimeoutException"/> if the loop does not go idle within
    /// <paramref name="timeout"/>.
    /// </summary>
    /// <param name="timeout">
    /// The maximum amount of time to wait. A value of
    /// <see cref="Timeout.InfiniteTimeSpan"/> waits indefinitely.
    /// </param>
    /// <returns>A task that completes when the loop is idle.</returns>
    /// <exception cref="TimeoutException">
    /// The loop did not reach an idle point within <paramref name="timeout"/>.
    /// </exception>
    Task WaitForIdleAsync(TimeSpan timeout);
}
