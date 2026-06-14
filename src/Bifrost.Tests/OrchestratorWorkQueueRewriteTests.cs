// =============================================================================
// <copyright file="OrchestratorWorkQueueRewriteTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
using System.Reflection;

using Bifrost.Core;
using Bifrost.Queues;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests;

/// <summary>
/// Tests for the T17 convergence rewrite: <see cref="WorkOrchestrator{TWork}"/> consumes
/// an <see cref="IWorkQueue{T}"/> (concretely a FIFO channel binding) instead of holding
/// direct <c>System.Threading.Channels</c> plumbing.
/// </summary>
/// <remarks>
/// <para>
/// The existing orchestrator suite is the behavior-preservation harness; the tests here
/// pin the structural change (queue field is <see cref="IWorkQueue{T}"/>-typed, no
/// channel fields remain), the canonical wait/try-dequeue worker loop shared by static
/// and dynamic workers, and the internal queue-wait observation hook that T24 will wire
/// into OpenTelemetry.
/// </para>
/// </remarks>
public class OrchestratorWorkQueueRewriteTests
{
    /// <summary>
    /// Per-test ceiling for waits that are expected to complete promptly.
    /// </summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Verifies that the orchestrator holds its queue as an <see cref="IWorkQueue{T}"/>
    /// and that no direct <c>System.Threading.Channels</c> field remains — the worker
    /// loop must consume the strategy contract, not a channel reader.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    [Test]
    public async Task WorkerLoop_UsesWaitTryDequeue_NotChannelReader()
    {
        // Arrange
        var fields = typeof(WorkOrchestrator<string>).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // Act
        var queueFieldCount = fields.Count(
            static f => typeof(IWorkQueue<WorkEnvelope<string>>).IsAssignableFrom(f.FieldType));
        var channelFieldCount = fields.Count(
            static f => f.FieldType.Namespace == "System.Threading.Channels");

        // Assert — exactly one IWorkQueue-typed field, zero channel-typed fields.
        await Assert.That(queueFieldCount).IsEqualTo(1);
        await Assert.That(channelFieldCount).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that dynamic workers created via
    /// <see cref="WorkOrchestrator{TWork}.CreateWorkerFunction()"/> consume the same
    /// <see cref="IWorkQueue{T}"/> as the enqueue path: a worker started before any
    /// enqueue processes both an orchestrator-enqueued item and an envelope injected
    /// directly into the internal queue.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    [Test]
    public async Task CreateWorkerFunction_DynamicWorkers_RouteThroughWorkQueue()
    {
        // Arrange — no static workers; the dynamic worker is the only consumer.
        var handler = new CountingHandler(expected: 2);
        var options = Options.Create(new WorkOrchestratorOptions { WorkerCount = 0 });
        await using var orchestrator = new WorkOrchestrator<string>(
            handler, options, NullLogger<WorkOrchestrator<string>>.Instance);

        var workerFunc = orchestrator.CreateWorkerFunction();
        using var cts = new CancellationTokenSource();

        // Act — start the dynamic worker first, then feed both enqueue surfaces.
        var workerTask = Task.Run(() => workerFunc("DynamicWorker", cts.Token), cts.Token);

        var enqueued = await orchestrator.EnqueueAsync("after-start").ConfigureAwait(false);
        await Assert.That(enqueued).IsEqualTo(EnqueueResult.Accepted);

        var envelope = new WorkEnvelope<string>(
            "via-queue", WorkClass.Batch, TimeProvider.System.GetTimestamp());
        var injected = orchestrator.WorkQueue.TryEnqueue(envelope);
        await Assert.That(injected).IsTrue();

        await handler.Done.WaitAsync(WaitTimeout).ConfigureAwait(false);

        // Assert — both items flowed through the same IWorkQueue to the dynamic worker.
        await Assert.That(handler.Processed).Contains("after-start");
        await Assert.That(handler.Processed).Contains("via-queue");

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(workerTask, Task.Delay(WaitTimeout)).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that <see cref="WorkOrchestrator{TWork}.PendingCount"/> delegates to the
    /// internal queue's <see cref="IWorkQueue{T}.Count"/>.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    [Test]
    public async Task PendingCount_DelegatesToQueueCount()
    {
        // Arrange — no workers, so enqueued items stay queued and countable.
        var handler = new CountingHandler(expected: 1);
        var options = Options.Create(new WorkOrchestratorOptions { WorkerCount = 0 });
        await using var orchestrator = new WorkOrchestrator<string>(
            handler, options, NullLogger<WorkOrchestrator<string>>.Instance);

        // Act
        await orchestrator.EnqueueAsync("a").ConfigureAwait(false);
        await orchestrator.EnqueueAsync("b").ConfigureAwait(false);
        await orchestrator.EnqueueAsync("c").ConfigureAwait(false);

        // Assert — PendingCount reads through to the queue's count, before and after
        // a direct dequeue.
        await Assert.That(orchestrator.PendingCount).IsEqualTo(3);
        await Assert.That(orchestrator.PendingCount).IsEqualTo(orchestrator.WorkQueue.Count);

        var dequeued = orchestrator.TryReadEnvelope(out _);
        await Assert.That(dequeued).IsTrue();
        await Assert.That(orchestrator.PendingCount).IsEqualTo(2);
        await Assert.That(orchestrator.PendingCount).IsEqualTo(orchestrator.WorkQueue.Count);
    }

    /// <summary>
    /// Verifies that <see cref="WorkOrchestrator{TWork}.DrainAsync"/> completes the
    /// queue (no new admissions) while workers finish all residual items before the
    /// drain completes.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    [Test]
    public async Task DrainAsync_CompletesQueue_WorkersFinishResidualItems()
    {
        // Arrange — single worker parked on the first item; four residual items queued.
        var handler = new GateCountingHandler();
        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 128, WorkerCount = 1 });
        var orchestrator = new WorkOrchestrator<string>(
            handler, options, NullLogger<WorkOrchestrator<string>>.Instance);

        await orchestrator.EnqueueAsync("item-0").ConfigureAwait(false);
        await handler.Started.WaitAsync(WaitTimeout).ConfigureAwait(false);
        for (var i = 1; i < 5; i++)
        {
            var result = await orchestrator.EnqueueAsync($"item-{i}").ConfigureAwait(false);
            await Assert.That(result).IsEqualTo(EnqueueResult.Accepted);
        }

        // Act — drain completes the queue; the released worker finishes all residue.
        var drainTask = orchestrator.DrainAsync();
        handler.Release();
        await drainTask.WaitAsync(WaitTimeout).ConfigureAwait(false);

        // Assert — every residual item was processed and the queue rejects new work.
        await Assert.That(handler.ProcessedCount).IsEqualTo(5);
        await Assert.That(orchestrator.PendingCount).IsEqualTo(0);
        await Assert.That(orchestrator.TryEnqueue("late")).IsFalse();

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that <see cref="WorkOrchestrator{TWork}.StopAsync"/> and
    /// <see cref="WorkOrchestrator{TWork}.DisposeAsync"/> preserve their semantics on
    /// the rewritten queue: the shutdown token cancels parked handlers so StopAsync
    /// completes well inside its graceful window, post-stop admissions are rejected as
    /// <see cref="RejectionReason.Shutdown"/>, and DisposeAsync stays bounded.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    [Test]
    public async Task StopAsync_DisposeAsync_SemanticsPreserved()
    {
        // Arrange — worker parked in a handler that honors cancellation.
        var handler = new ParkUntilCancelledHandler();
        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 8, WorkerCount = 1 });
        var orchestrator = new WorkOrchestrator<string>(
            handler, options, NullLogger<WorkOrchestrator<string>>.Instance);
        var shutdownToken = orchestrator.GetShutdownToken();

        await orchestrator.EnqueueAsync("parked").ConfigureAwait(false);
        await handler.Started.WaitAsync(WaitTimeout).ConfigureAwait(false);

        // Act — StopAsync cancels workers; the cooperative handler unblocks promptly,
        // well inside the 30-second graceful-shutdown window.
        await orchestrator.StopAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // Assert — shutdown token fired and the queue is closed to new work.
        await Assert.That(shutdownToken.IsCancellationRequested).IsTrue();
        var afterStop = await orchestrator.EnqueueAsync("after-stop").ConfigureAwait(false);
        await Assert.That(afterStop.IsAccepted).IsFalse();
        await Assert.That(afterStop.Reason).IsEqualTo(RejectionReason.Shutdown);
        await Assert.That(orchestrator.TryEnqueue("after-stop-try")).IsFalse();

        // DisposeAsync after StopAsync stays bounded.
        await orchestrator.DisposeAsync().AsTask().WaitAsync(WaitTimeout).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that the internal queue-wait hook fires at dequeue with the envelope's
    /// <see cref="WorkClass"/> and an elapsed duration computed via
    /// <see cref="TimeProvider.GetElapsedTime(long)"/> from the envelope's enqueue
    /// timestamp — made exact here with a <see cref="FakeTimeProvider"/>.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    [Test]
    public async Task QueueWaitHook_ObservedAtDequeue()
    {
        // Arrange — FakeTimeProvider makes the queue-wait duration deterministic.
        var fakeTime = new FakeTimeProvider();
        var handler = new CountingHandler(expected: 1);
        var options = Options.Create(new WorkOrchestratorOptions { WorkerCount = 0 });
        await using var orchestrator = new WorkOrchestrator<string>(
            handler, options, NullLogger<WorkOrchestrator<string>>.Instance, fakeTime);

        var observed = new TaskCompletionSource<(WorkClass Class, TimeSpan Waited)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        orchestrator.QueueWaitObserved = (workClass, waited) => observed.TrySetResult((workClass, waited));

        var enqueued = await orchestrator.EnqueueAsync("waited-item", WorkClass.Interactive).ConfigureAwait(false);
        await Assert.That(enqueued).IsEqualTo(EnqueueResult.Accepted);
        fakeTime.Advance(TimeSpan.FromSeconds(5));

        // Act — start a worker; dequeueing the envelope must fire the hook.
        var workerFunc = orchestrator.CreateWorkerFunction();
        using var cts = new CancellationTokenSource();
        var workerTask = Task.Run(() => workerFunc("HookWorker", cts.Token), cts.Token);

        var (workClass, waited) = await observed.Task.WaitAsync(WaitTimeout).ConfigureAwait(false);

        // Assert — the hook reports the envelope's class and its exact queue wait.
        await Assert.That(workClass).IsEqualTo(WorkClass.Interactive);
        await Assert.That(waited).IsEqualTo(TimeSpan.FromSeconds(5));

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(workerTask, Task.Delay(WaitTimeout)).ConfigureAwait(false);
    }

    /// <summary>
    /// Handler that records processed items and completes <see cref="Done"/> once the
    /// expected number of items has been handled.
    /// </summary>
    private sealed class CountingHandler : IWorkHandler<string>
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _processed = new();
        private readonly int _expected;
        private int _count;

        /// <summary>
        /// Initializes a new instance of the <see cref="CountingHandler"/> class.
        /// </summary>
        /// <param name="expected">The number of handled items that completes <see cref="Done"/>.</param>
        public CountingHandler(int expected) => _expected = expected;

        /// <summary>Gets a task that completes when the expected item count is reached.</summary>
        public Task Done => _done.Task;

        /// <summary>Gets the items handled so far.</summary>
        public IReadOnlyCollection<string> Processed => _processed;

        /// <inheritdoc/>
        public ValueTask HandleAsync(string work, CancellationToken ct)
        {
            _processed.Enqueue(work);
            if (Interlocked.Increment(ref _count) >= _expected)
            {
                _done.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Handler that signals when the first item enters, parks all invocations on a gate,
    /// and counts items that complete after release — enabling deterministic residual
    /// drain assertions.
    /// </summary>
    private sealed class GateCountingHandler : IWorkHandler<string>
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _processed;

        /// <summary>Gets a task that completes when the first item enters the handler.</summary>
        public Task Started => _started.Task;

        /// <summary>Gets the number of items fully processed (post-gate).</summary>
        public int ProcessedCount => Volatile.Read(ref _processed);

        /// <summary>Releases all current and future handler invocations.</summary>
        public void Release() => _gate.TrySetResult();

        /// <inheritdoc/>
        public async ValueTask HandleAsync(string work, CancellationToken ct)
        {
            _started.TrySetResult();
            await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
            Interlocked.Increment(ref _processed);
        }
    }

    /// <summary>
    /// Handler that signals entry and then parks until the worker's cancellation token
    /// fires, for exercising cooperative-shutdown paths.
    /// </summary>
    private sealed class ParkUntilCancelledHandler : IWorkHandler<string>
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a task that completes when the first item enters the handler.</summary>
        public Task Started => _started.Task;

        /// <inheritdoc/>
        public async ValueTask HandleAsync(string work, CancellationToken ct)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
    }
}
