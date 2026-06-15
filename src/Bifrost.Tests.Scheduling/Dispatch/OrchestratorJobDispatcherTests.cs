// =============================================================================
// <copyright file="OrchestratorJobDispatcherTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;

using Bifrost.Core;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;
using Bifrost.Scheduling.Dispatch;

using NSubstitute;

namespace Bifrost.Tests.Scheduling.Dispatch;

/// <summary>
/// Tests for <see cref="OrchestratorJobDispatcher{TWork}"/> (Task 23, DR-4): the
/// integration with Bifrost priority dispatch. It builds work from the fire
/// context and enqueues it on the <see cref="IWorkOrchestrator{TWork}"/> under a
/// configured <see cref="WorkClass"/>, surfacing an admission rejection as a
/// <see cref="JobFireFailedEvent"/> without throwing.
/// </summary>
public sealed class OrchestratorJobDispatcherTests
{
    private static readonly DateTimeOffset FireTime =
        new(2026, 6, 13, 2, 0, 0, TimeSpan.Zero);

    private static JobFireContext Context(string jobName = "job") =>
        new(jobName, FireTime, FireTime.AddHours(1), new StubServiceProvider());

    /// <summary>
    /// Verifies the dispatcher calls the fire function to produce the work item.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchAsync_CallsFireFuncToProduceWork()
    {
        var orchestrator = Substitute.For<IWorkOrchestrator<Payload>>();
        orchestrator
            .EnqueueAsync(Arg.Any<Payload>(), Arg.Any<WorkClass>(), Arg.Any<CancellationToken>())
            .Returns(EnqueueResult.Accepted);

        var fireCalls = 0;
        var dispatcher = new OrchestratorJobDispatcher<Payload>(
            _ =>
            {
                fireCalls++;
                return new Payload("built");
            },
            orchestrator,
            WorkClass.Batch,
            sink:new RecordingSchedulerEventSink());

        await dispatcher.DispatchAsync(Context(), CancellationToken.None).ConfigureAwait(false);

        await Assert.That(fireCalls).IsEqualTo(1);
        _ = orchestrator.Received(1).EnqueueAsync(
            Arg.Is<Payload>(p => p.Value == "built"),
            Arg.Any<WorkClass>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies the work is enqueued under <see cref="WorkClass.Batch"/> by
    /// default, so scheduled batch jobs never starve interactive work.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchAsync_CallsEnqueueAsync_WithBatchWorkClassByDefault()
    {
        var orchestrator = Substitute.For<IWorkOrchestrator<Payload>>();
        orchestrator
            .EnqueueAsync(Arg.Any<Payload>(), Arg.Any<WorkClass>(), Arg.Any<CancellationToken>())
            .Returns(EnqueueResult.Accepted);

        var dispatcher = new OrchestratorJobDispatcher<Payload>(
            _ => new Payload("w"),
            orchestrator,
            WorkClass.Batch,
            sink:new RecordingSchedulerEventSink());

        await dispatcher.DispatchAsync(Context(), CancellationToken.None).ConfigureAwait(false);

        _ = orchestrator.Received(1).EnqueueAsync(
            Arg.Any<Payload>(),
            WorkClass.Batch,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies a configured work-class override (e.g. <see cref="WorkClass.Interactive"/>)
    /// is honored over the default.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchAsync_HonorsConfiguredWorkClassOverride()
    {
        var orchestrator = Substitute.For<IWorkOrchestrator<Payload>>();
        orchestrator
            .EnqueueAsync(Arg.Any<Payload>(), Arg.Any<WorkClass>(), Arg.Any<CancellationToken>())
            .Returns(EnqueueResult.Accepted);

        var dispatcher = new OrchestratorJobDispatcher<Payload>(
            _ => new Payload("w"),
            orchestrator,
            WorkClass.Interactive,
            new RecordingSchedulerEventSink());

        await dispatcher.DispatchAsync(Context(), CancellationToken.None).ConfigureAwait(false);

        _ = orchestrator.Received(1).EnqueueAsync(
            Arg.Any<Payload>(),
            WorkClass.Interactive,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies an admission rejection (capacity / watermark / shutdown) is a
    /// failed fire: it publishes a <see cref="JobFireFailedEvent"/> carrying the
    /// rejection reason, does NOT throw, and — because rejection happens at
    /// admission — leaves the job's next occurrence unaffected (the dispatcher
    /// completes normally so the tick loop schedules the next fire as usual).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchAsync_EnqueueRejected_PublishesJobFireFailedEvent_DoesNotThrow_NextOccurrenceUnaffected()
    {
        var orchestrator = Substitute.For<IWorkOrchestrator<Payload>>();
        orchestrator
            .EnqueueAsync(Arg.Any<Payload>(), Arg.Any<WorkClass>(), Arg.Any<CancellationToken>())
            .Returns(EnqueueResult.Rejected(RejectionReason.WatermarkExceeded));

        var sink = new RecordingSchedulerEventSink();
        var dispatcher = new OrchestratorJobDispatcher<Payload>(
            _ => new Payload("w"),
            orchestrator,
            WorkClass.Batch,
            sink:sink);

        // Must complete normally — a rejected admission is not an exception.
        await dispatcher.DispatchAsync(Context("watermarked"), CancellationToken.None).ConfigureAwait(false);

        await Assert.That(sink.Any<JobFireFailedEvent>()).IsTrue();
        var failed = sink.Single<JobFireFailedEvent>();
        await Assert.That(failed.JobName).IsEqualTo("watermarked");
        await Assert.That(failed.FiredAt).IsEqualTo(FireTime);
        await Assert.That(failed.Exception).IsNull();
        await Assert.That(failed.Reason).IsEqualTo(RejectionReason.WatermarkExceeded.ToString());
    }

    /// <summary>
    /// Verifies an exception thrown by the fire function propagates out of
    /// <see cref="OrchestratorJobDispatcher{TWork}.DispatchAsync"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchAsync_FireFuncThrows_BubblesException()
    {
        var orchestrator = Substitute.For<IWorkOrchestrator<Payload>>();
        var boom = new InvalidOperationException("fire boom");

        var dispatcher = new OrchestratorJobDispatcher<Payload>(
            _ => throw boom,
            orchestrator,
            WorkClass.Batch,
            sink:new RecordingSchedulerEventSink());

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dispatcher.DispatchAsync(Context(), CancellationToken.None).ConfigureAwait(false));

        await Assert.That(thrown).IsSameReferenceAs(boom);

        // The fire never reached admission.
        _ = orchestrator.DidNotReceive().EnqueueAsync(
            Arg.Any<Payload>(), Arg.Any<WorkClass>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies caller-token cancellation propagates: when
    /// <see cref="IWorkOrchestrator{TWork}.EnqueueAsync"/> throws an
    /// <see cref="OperationCanceledException"/>, it is not swallowed — unlike an
    /// admission rejection, which does not throw.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchAsync_EnqueueThrowsOperationCanceled_Bubbles()
    {
        var orchestrator = Substitute.For<IWorkOrchestrator<Payload>>();
        orchestrator
            .EnqueueAsync(Arg.Any<Payload>(), Arg.Any<WorkClass>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<EnqueueResult>>(_ => throw new OperationCanceledException());

        var sink = new RecordingSchedulerEventSink();
        var dispatcher = new OrchestratorJobDispatcher<Payload>(
            _ => new Payload("w"),
            orchestrator,
            WorkClass.Batch,
            sink:sink);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await dispatcher.DispatchAsync(Context(), CancellationToken.None).ConfigureAwait(false));

        // A cancelled enqueue is not a failed fire — no event is published.
        await Assert.That(sink.Any<JobFireFailedEvent>()).IsFalse();
    }

    /// <summary>
    /// Verifies the fire function receives the fire context carrying the scheduled
    /// occurrence time (the idempotency key), not the wall-clock instant.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchAsync_FireFuncReceivesContext_WithScheduledFireTime()
    {
        var orchestrator = Substitute.For<IWorkOrchestrator<Payload>>();
        orchestrator
            .EnqueueAsync(Arg.Any<Payload>(), Arg.Any<WorkClass>(), Arg.Any<CancellationToken>())
            .Returns(EnqueueResult.Accepted);

        JobFireContext? seen = null;
        var dispatcher = new OrchestratorJobDispatcher<Payload>(
            ctx =>
            {
                seen = ctx;
                return new Payload(ctx.JobName);
            },
            orchestrator,
            WorkClass.Batch,
            sink:new RecordingSchedulerEventSink());

        var context = Context("contextual");
        await dispatcher.DispatchAsync(context, CancellationToken.None).ConfigureAwait(false);

        await Assert.That(seen).IsNotNull();
        await Assert.That(seen!.Value.JobName).IsEqualTo("contextual");
        await Assert.That(seen.Value.FireTime).IsEqualTo(FireTime);
        await Assert.That(seen.Value).IsEqualTo(context);
    }

    /// <summary>
    /// Verifies the <c>sink</c> ctor parameter is required — it has no default value
    /// (H1). Defaulting it to <c>null!</c> let <c>new OrchestratorJobDispatcher&lt;T&gt;(fire, orch)</c>
    /// compile and then throw <see cref="ArgumentNullException"/> at runtime: a
    /// required dependency that looked optional. Asserting the parameter is
    /// non-optional locks in that omitting the sink is now a compile error.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Ctor_SinkParameter_IsRequired_HasNoDefaultValue()
    {
        var ctor = typeof(OrchestratorJobDispatcher<Payload>)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single();

        var sinkParameter = ctor.GetParameters().Single(p => p.Name == "sink");

        // A required dependency must not carry a compiler-supplied default, so an
        // omitted sink fails to compile rather than throwing at runtime.
        await Assert.That(sinkParameter.HasDefaultValue).IsFalse();
        await Assert.That(sinkParameter.IsOptional).IsFalse();
        await Assert.That(sinkParameter.ParameterType).IsEqualTo(typeof(ISchedulerEventSink));
    }

    /// <summary>
    /// Verifies the public ctor with a real sink supplied constructs a working
    /// dispatcher with no silent-null sink path (H1): a rejected admission reaches
    /// the supplied sink as a <see cref="JobFireFailedEvent"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Ctor_WithRealSink_ConstructsWorkingDispatcher()
    {
        var orchestrator = Substitute.For<IWorkOrchestrator<Payload>>();
        orchestrator
            .EnqueueAsync(Arg.Any<Payload>(), Arg.Any<WorkClass>(), Arg.Any<CancellationToken>())
            .Returns(EnqueueResult.Rejected(RejectionReason.CapacityExceeded));

        var sink = new RecordingSchedulerEventSink();
        var dispatcher = new OrchestratorJobDispatcher<Payload>(
            _ => new Payload("w"),
            orchestrator,
            WorkClass.Batch,
            sink);

        await dispatcher.DispatchAsync(Context(), CancellationToken.None).ConfigureAwait(false);

        await Assert.That(sink.Any<JobFireFailedEvent>()).IsTrue();
    }

    /// <summary>
    /// A simple work payload for the orchestrator substitute. Public so
    /// NSubstitute's Castle DynamicProxy can proxy
    /// <see cref="IWorkOrchestrator{TWork}"/> closed over it.
    /// </summary>
    public sealed record Payload(string Value);

    /// <summary>
    /// A minimal non-null <see cref="IServiceProvider"/> to carry through the
    /// fire context.
    /// </summary>
    private sealed class StubServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
