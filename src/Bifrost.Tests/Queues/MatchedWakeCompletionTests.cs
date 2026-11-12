// =============================================================================
// <copyright file="MatchedWakeCompletionTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Queues;

namespace Bifrost.Tests.Queues;

/// <summary>
/// A matched-wake take must not consume the permit <c>Complete</c> publishes for a parked consumer.
/// </summary>
public sealed class MatchedWakeCompletionTests
{
    private const long TimestampFrequency = 10_000_000;

    /// <summary>
    /// Completion that overlaps a relaxed matched-wake take still wakes a parked consumer.
    /// </summary>
    [Test]
    public async Task RelaxedMatchedWake_LeavesTheCompletionPermit()
    {
        var queue = new ConcurrentPriorityWorkQueue<int>(8, new PriorityDispatchOptions(), TimestampFrequency);
        await AssertCompletionPermitSurvives(
            hook => queue.BeforeMatchedWakeForTest = hook,
            hook => queue.BeforeCompletionLockForTest = hook,
            queue.ConsumeMatchedWake,
            queue.Complete,
            queue.WaitToDequeueAsync).ConfigureAwait(false);
    }

    /// <summary>
    /// Completion that overlaps an exact matched-wake take still wakes a parked consumer.
    /// </summary>
    [Test]
    public async Task ExactMatchedWake_LeavesTheCompletionPermit()
    {
        var queue = new LockingPriorityWorkQueue<int>(8, new PriorityDispatchOptions(), TimestampFrequency);
        await AssertCompletionPermitSurvives(
            hook => queue.BeforeMatchedWakeForTest = hook,
            hook => queue.BeforeCompletionLockForTest = hook,
            queue.ConsumeMatchedWake,
            queue.Complete,
            queue.WaitToDequeueAsync).ConfigureAwait(false);
    }

    private static async Task AssertCompletionPermitSurvives(
        Action<Action> install,
        Action<Action> installCompletion,
        Action consumeMatchedWake,
        Action complete,
        Func<CancellationToken, ValueTask<bool>> wait)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        install(() =>
        {
            entered.TrySetResult();
            release.Task.Wait(TimeSpan.FromSeconds(5));
        });
        installCompletion(() => completionStarted.TrySetResult());

        var consume = Task.Run(consumeMatchedWake);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var parked = wait(CancellationToken.None).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        var completing = Task.Run(complete);
        await completionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(completing.IsCompleted).IsFalse();

        release.TrySetResult();
        await consume.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await completing.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var dequeued = await parked.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(dequeued).IsFalse();
    }
}
