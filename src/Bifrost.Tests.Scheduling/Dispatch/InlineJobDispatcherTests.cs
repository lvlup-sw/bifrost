// =============================================================================
// <copyright file="InlineJobDispatcherTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Dispatch;

namespace Bifrost.Tests.Scheduling.Dispatch;

/// <summary>
/// Tests for <see cref="InlineJobDispatcher"/> (Task 24, DR-4): runs a supplied
/// delegate when a job fires, guaranteeing it executes on a thread-pool thread
/// rather than the tick thread.
/// </summary>
public sealed class InlineJobDispatcherTests
{
    private static readonly DateTimeOffset FireTime =
        new(2026, 6, 13, 2, 0, 0, TimeSpan.Zero);

    private static JobFireContext Context(string jobName = "job") =>
        new(jobName, FireTime, FireTime.AddHours(1), new StubServiceProvider());

    /// <summary>
    /// Verifies the dispatcher invokes the supplied delegate.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchAsync_InvokesDelegate()
    {
        var invoked = 0;
        var dispatcher = new InlineJobDispatcher((_, _) =>
        {
            Interlocked.Increment(ref invoked);
            return ValueTask.CompletedTask;
        });

        await dispatcher.DispatchAsync(Context(), CancellationToken.None).ConfigureAwait(false);

        await Assert.That(invoked).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies the delegate runs on a thread-pool thread, not the calling (tick)
    /// thread.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchAsync_RunsDelegateOnPoolThread_NotCallingThread()
    {
        var callerThreadId = Environment.CurrentManagedThreadId;
        var ranOnThreadId = 0;
        var ranOnPoolThread = false;

        var dispatcher = new InlineJobDispatcher((_, _) =>
        {
            ranOnThreadId = Environment.CurrentManagedThreadId;
            ranOnPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            return ValueTask.CompletedTask;
        });

        await dispatcher.DispatchAsync(Context(), CancellationToken.None).ConfigureAwait(false);

        await Assert.That(ranOnThreadId).IsNotEqualTo(callerThreadId);
        await Assert.That(ranOnPoolThread).IsTrue();
    }

    /// <summary>
    /// Verifies an exception thrown by the delegate bubbles out of the in-flight
    /// dispatch task.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchAsync_DelegateThrows_BubblesToInFlightTask()
    {
        var boom = new InvalidOperationException("inline boom");
        var dispatcher = new InlineJobDispatcher((_, _) => throw boom);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dispatcher.DispatchAsync(Context(), CancellationToken.None).ConfigureAwait(false));

        await Assert.That(thrown).IsSameReferenceAs(boom);
    }

    /// <summary>
    /// Verifies the cancellation token flows through to the delegate.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchAsync_PropagatesCancellationTokenToDelegate()
    {
        using var cts = new CancellationTokenSource();
        var seen = CancellationToken.None;

        var dispatcher = new InlineJobDispatcher((_, ct) =>
        {
            seen = ct;
            return ValueTask.CompletedTask;
        });

        await dispatcher.DispatchAsync(Context(), cts.Token).ConfigureAwait(false);

        await Assert.That(seen).IsEqualTo(cts.Token);
    }

    /// <summary>
    /// Verifies a cancelled token observed by the delegate can trigger an
    /// <see cref="OperationCanceledException"/> that bubbles out.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchAsync_CancelledToken_DelegateCanThrowOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync().ConfigureAwait(false);

        var dispatcher = new InlineJobDispatcher((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        });

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await dispatcher.DispatchAsync(Context(), cts.Token).ConfigureAwait(false));
    }

    /// <summary>
    /// Verifies the fire context passes through to the delegate unchanged.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchAsync_PassesContextThrough()
    {
        JobFireContext? seen = null;
        var dispatcher = new InlineJobDispatcher((ctx, _) =>
        {
            seen = ctx;
            return ValueTask.CompletedTask;
        });

        var context = Context("contextual");
        await dispatcher.DispatchAsync(context, CancellationToken.None).ConfigureAwait(false);

        await Assert.That(seen).IsNotNull();
        await Assert.That(seen!.Value).IsEqualTo(context);
        await Assert.That(seen.Value.FireTime).IsEqualTo(FireTime);
    }

    /// <summary>
    /// A minimal non-null <see cref="IServiceProvider"/> to carry through the
    /// fire context.
    /// </summary>
    private sealed class StubServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
