// =============================================================================
// <copyright file="OrchestratorEnqueueSurfaceTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

namespace Bifrost.Tests;

/// <summary>
/// Tests for the v0.5.0 breaking enqueue surface: <see cref="EnqueueResult"/>
/// admission outcomes, the optional <see cref="WorkClass"/> parameter, and the
/// removal of the <c>Writer</c> escape hatch.
/// </summary>
public class OrchestratorEnqueueSurfaceTests
{
    /// <summary>
    /// Verifies that the default FIFO path returns <see cref="EnqueueResult.Accepted"/>,
    /// including after waiting for capacity under backpressure.
    /// </summary>
    /// <remarks>
    /// The orchestrator has one worker and capacity 1, and the handler parks on a gate. The sentinel item
    /// parks the single worker inside the handler, so the channel can fill. The next enqueue must then
    /// wait for space, and it succeeds when capacity frees up.
    /// </remarks>
    [Test]
    public async Task EnqueueAsync_Fifo_ReturnsAcceptedAfterWait()
    {
        var handler = new GateHandler();
        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 1, WorkerCount = 1 });
        var logger = Substitute.For<ILogger<WorkOrchestrator<string>>>();
        await using var orchestrator = new WorkOrchestrator<string>(handler, options, logger);

        var sentinelResult = await orchestrator.EnqueueAsync("sentinel").ConfigureAwait(false);
        await Assert.That(sentinelResult).IsEqualTo(EnqueueResult.Accepted);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var fillResult = await orchestrator.EnqueueAsync("fill").ConfigureAwait(false);
        await Assert.That(fillResult).IsEqualTo(EnqueueResult.Accepted);

        var waiting = orchestrator.EnqueueAsync("waited").AsTask();
        await Assert.That(waiting.IsCompleted).IsFalse();

        handler.Release();
        var waitedResult = await waiting.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        await Assert.That(waitedResult).IsEqualTo(EnqueueResult.Accepted);
    }

    /// <summary>
    /// Verifies that enqueueing after shutdown returns
    /// <see cref="EnqueueResult.Rejected"/> with <see cref="RejectionReason.Shutdown"/>
    /// and never throws <see cref="System.Threading.Channels.ChannelClosedException"/>.
    /// </summary>
    /// <remarks>
    /// A shut-down queue that refuses work gives an admission outcome (a value). Caller cancellation is
    /// different: it gives an exception, as the next test shows.
    /// </remarks>
    [Test]
    public async Task EnqueueAsync_AfterShutdown_ReturnsRejectedShutdown()
    {
        var handler = Substitute.For<IWorkHandler<string>>();
        var options = Options.Create(new WorkOrchestratorOptions { WorkerCount = 0 });
        var logger = Substitute.For<ILogger<WorkOrchestrator<string>>>();

        await using var orchestrator = new WorkOrchestrator<string>(handler, options, logger);
        await orchestrator.AbortAsync().ConfigureAwait(false);

        var afterStop = await orchestrator.EnqueueAsync("after-stop").ConfigureAwait(false);

        await Assert.That(afterStop.IsAccepted).IsFalse();
        await Assert.That(afterStop.Reason).IsEqualTo(RejectionReason.Shutdown);
    }

    /// <summary>
    /// Verifies the R1 cancellation contract: a canceled caller token surfaces an
    /// <see cref="OperationCanceledException"/> — distinct from
    /// <see cref="RejectionReason.Shutdown"/> — matching the TAP / <c>ChannelWriter</c>
    /// precedent. The orchestrator here is live (not shut down), so the only reason
    /// the enqueue does not succeed is the caller's cancellation.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_CallerTokenCanceled_ThrowsOperationCanceled()
    {
        var handler = Substitute.For<IWorkHandler<string>>();
        var options = Options.Create(new WorkOrchestratorOptions { WorkerCount = 0 });
        var logger = Substitute.For<ILogger<WorkOrchestrator<string>>>();

        await using var orchestrator = new WorkOrchestrator<string>(handler, options, logger);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync().ConfigureAwait(false);

        await Assert.That(() => orchestrator.EnqueueAsync("cancelled", ct: cts.Token).AsTask())
            .Throws<OperationCanceledException>();
    }

    /// <summary>
    /// Verifies that work enqueued without an explicit class is enveloped with
    /// <see cref="WorkClass.Default"/>, and that a tagged enqueue round-trips
    /// its work class through the envelope.
    /// </summary>
    /// <remarks>
    /// The orchestrator has no workers, so the envelopes stay observable in the channel.
    /// <c>TryEnqueue</c> also defaults to <c>WorkClass.Default</c>.
    /// </remarks>
    [Test]
    public async Task EnqueueAsync_DefaultsWorkClassDefault()
    {
        var handler = Substitute.For<IWorkHandler<string>>();
        var options = Options.Create(new WorkOrchestratorOptions { WorkerCount = 0 });
        var logger = Substitute.For<ILogger<WorkOrchestrator<string>>>();
        await using var orchestrator = new WorkOrchestrator<string>(handler, options, logger);

        var plain = await orchestrator.EnqueueAsync("plain").ConfigureAwait(false);
        await Assert.That(plain).IsEqualTo(EnqueueResult.Accepted);

        await Assert.That(orchestrator.TryReadEnvelope(out var plainEnvelope)).IsTrue();
        await Assert.That(plainEnvelope.Work).IsEqualTo("plain");
        await Assert.That(plainEnvelope.Class).IsEqualTo(WorkClass.Default);
        await Assert.That(plainEnvelope.EnqueuedAtTicks).IsNotEqualTo(0L);

        var tagged = await orchestrator.EnqueueAsync("tagged", WorkClass.Interactive).ConfigureAwait(false);
        await Assert.That(tagged).IsEqualTo(EnqueueResult.Accepted);

        await Assert.That(orchestrator.TryReadEnvelope(out var taggedEnvelope)).IsTrue();
        await Assert.That(taggedEnvelope.Work).IsEqualTo("tagged");
        await Assert.That(taggedEnvelope.Class).IsEqualTo(WorkClass.Interactive);

        await Assert.That(orchestrator.TryEnqueue("try-plain")).IsTrue();
        await Assert.That(orchestrator.TryReadEnvelope(out var tryEnvelope)).IsTrue();
        await Assert.That(tryEnvelope.Class).IsEqualTo(WorkClass.Default);
    }

    /// <summary>
    /// Verifies that the <c>Writer</c> escape hatch has been removed from the
    /// orchestrator contract.
    /// </summary>
    [Test]
    public async Task Writer_Property_NoLongerExists()
    {
        var property = typeof(IWorkOrchestrator<string>).GetProperty("Writer");

        await Assert.That(property).IsNull();
    }

    /// <summary>
    /// Handler that signals when processing starts and blocks until released,
    /// allowing tests to park workers deterministically.
    /// </summary>
    private sealed class GateHandler : IWorkHandler<string>
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a task that completes when the first item enters the handler.</summary>
        public Task Started => _started.Task;

        /// <summary>Releases all current and future handler invocations.</summary>
        public void Release() => _gate.TrySetResult();

        /// <inheritdoc/>
        public async ValueTask HandleAsync(string work, CancellationToken ct)
        {
            _started.TrySetResult();
            await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
