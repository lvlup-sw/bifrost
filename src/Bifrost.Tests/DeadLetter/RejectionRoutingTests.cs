// =============================================================================
// <copyright file="RejectionRoutingTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.Metrics;

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.DeadLetter;
using Bifrost.Decorators;
using Bifrost.DependencyInjection;
using Bifrost.OpenTelemetry;
using Bifrost.Queues;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.DeadLetter;

/// <summary>
/// Tests for rejection routing (T23, DR-6): admission rejections from the
/// fail-fast priority strategies route to the dead-letter pathway when DLQ
/// infrastructure is configured — the same family as handler failures — and are
/// always counted in <c>bifrost.orchestrator.rejected</c>. Routing is
/// observability, not retry: the caller still receives the rejected
/// <see cref="EnqueueResult"/>, and the at-least-once execution contract is
/// unaffected because rejected work was never admitted.
/// </summary>
[Property("Category", "Unit")]
public class RejectionRoutingTests
{
    /// <summary>
    /// Verifies that a rejected enqueue on a DLQ-configured priority orchestrator
    /// lands in the dead-letter queue with the rejection-distinguishing marker
    /// (zero attempts + <see cref="WorkRejectedException"/> carrying the
    /// <see cref="RejectionReason"/>), while the caller still receives the
    /// rejected result — routing is observability, not retry.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// The priority strategy is at capacity (fail-fast admission), and the DLQ is configured. The
    /// single worker is parked inside a gated handler, so the queue state is deterministic while
    /// the test fills to capacity. The test parks the worker, fills to capacity, and then sheds one
    /// item. The caller must still receive the rejection (no swallowing). The shed item must be in
    /// the dead-letter pathway with the marker that identifies a rejection: <c>AttemptCount</c> 0
    /// (never admitted, never attempted) and a <c>WorkRejectedException</c> that carries the
    /// reason. At the end, the test releases the parked worker so that disposal does not wait.
    /// </remarks>
    [Test]
    public async Task EnqueueRejected_WithDlqDecorator_RoutesToDeadLetter()
    {
        var handler = new GateHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(handler);
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 2;
            opts.WorkerCount = 1;
            opts.DispatchStrategy = DispatchStrategy.PriorityRelaxed;
            opts.FullMode = QueueFullMode.Reject;
        })
        .WithDeadLetterQueue()
        .Build();

        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var dlq = provider.GetRequiredService<IDeadLetterQueue<string>>();

        var parked = await orchestrator.EnqueueAsync("park-worker", WorkClass.Interactive);
        await Assert.That(parked).IsEqualTo(EnqueueResult.Accepted);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var first = await orchestrator.EnqueueAsync("keep-1", WorkClass.Interactive);
        var second = await orchestrator.EnqueueAsync("keep-2", WorkClass.Interactive);
        var shed = await orchestrator.EnqueueAsync("shed-item", WorkClass.Interactive);

        await Assert.That(first).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(second).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(shed.IsAccepted).IsFalse();
        await Assert.That(shed.Reason).IsEqualTo(RejectionReason.CapacityExceeded);

        var entries = new List<DeadLetteredWork<string>>();
        await foreach (var entry in dlq.ReadAllAsync())
        {
            entries.Add(entry);
        }

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Work).IsEqualTo("shed-item");
        await Assert.That(entries[0].AttemptCount).IsEqualTo(0);
        var marker = entries[0].Exception as WorkRejectedException;
        await Assert.That(marker).IsNotNull();
        await Assert.That(marker!.Reason).IsEqualTo(RejectionReason.CapacityExceeded);

        handler.Release();
    }

    /// <summary>
    /// Verifies that without DLQ infrastructure a rejection surfaces only as the
    /// typed <see cref="EnqueueResult"/>: nothing is thrown, nothing else happens.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// The routing decorator has no DLQ services. The test fills to capacity and then sheds one
    /// item, and the enqueue must not throw. Only the typed rejection surfaces to the caller.
    /// </remarks>
    [Test]
    public async Task EnqueueRejected_WithoutDlq_SurfacesResultOnly()
    {
        var options = Options.Create(new WorkOrchestratorOptions
        {
            Capacity = 1,
            WorkerCount = 0,
            DispatchStrategy = DispatchStrategy.PriorityRelaxed,
            FullMode = QueueFullMode.Reject,
        });
        await using var orchestrator = new WorkOrchestrator<string>(
            Substitute.For<IWorkHandler<string>>(), options, NullLogger<WorkOrchestrator<string>>.Instance);
        var routing = new RejectionRoutingOrchestrator<string>(
            orchestrator,
            deadLetterQueue: null,
            notifier: null,
            NullLogger<RejectionRoutingOrchestrator<string>>.Instance);

        var accepted = await routing.EnqueueAsync("keep", WorkClass.Interactive);
        var shed = await routing.EnqueueAsync("shed", WorkClass.Interactive);

        await Assert.That(accepted).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(shed.IsAccepted).IsFalse();
        await Assert.That(shed.Reason).IsEqualTo(RejectionReason.CapacityExceeded);
    }

    /// <summary>
    /// Verifies that rejections surfaced through the boolean
    /// <see cref="IWorkWriter{TWork}.TryEnqueue"/> surface are observed too:
    /// the caller still receives <c>false</c>, and the rejection is counted and
    /// routed to the dead-letter pathway with the rejection-distinguishing marker.
    /// The boolean surface carries no <see cref="RejectionReason"/>, so a
    /// non-shutdown rejection is inferred as
    /// <see cref="RejectionReason.CapacityExceeded"/>.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// The capacity is 1 and there are no workers, so the second <c>TryEnqueue</c> rejects
    /// fail-fast. The test uses the boolean surface: it fills, then sheds. False surfaces to the
    /// caller. The rejection is counted with the inferred reason and routed with the marker
    /// (<c>AttemptCount</c> 0 + exception).
    /// </remarks>
    [Test]
    public async Task TryEnqueueRejected_WithDlq_IsCountedAndRouted()
    {
        var options = Options.Create(new WorkOrchestratorOptions
        {
            Capacity = 1,
            WorkerCount = 0,
            DispatchStrategy = DispatchStrategy.PriorityRelaxed,
            FullMode = QueueFullMode.Reject,
        });
        await using var orchestrator = new WorkOrchestrator<string>(
            Substitute.For<IWorkHandler<string>>(), options, NullLogger<WorkOrchestrator<string>>.Instance);
        var dlq = new DeadLetterQueue<string>(
            Options.Create(new DeadLetterQueueOptions()), NullLogger<DeadLetterQueue<string>>.Instance);
        var notifier = new DeadLetterNotifier<string>(NullLogger<DeadLetterNotifier<string>>.Instance);
        var routing = new RejectionRoutingOrchestrator<string>(
            orchestrator, dlq, notifier, NullLogger<RejectionRoutingOrchestrator<string>>.Instance);

        var observed = new List<(WorkClass Class, RejectionReason Reason)>();
        routing.RejectionObserved = (workClass, reason) => observed.Add((workClass, reason));

        var kept = routing.TryEnqueue("keep", WorkClass.Interactive);
        var shed = routing.TryEnqueue("shed", WorkClass.Interactive);

        await Assert.That(kept).IsTrue();
        await Assert.That(shed).IsFalse();
        await Assert.That(observed.Count).IsEqualTo(1);
        await Assert.That(observed[0].Class).IsEqualTo(WorkClass.Interactive);
        await Assert.That(observed[0].Reason).IsEqualTo(RejectionReason.CapacityExceeded);

        var entries = new List<DeadLetteredWork<string>>();
        await foreach (var entry in dlq.ReadAllAsync())
        {
            entries.Add(entry);
        }

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Work).IsEqualTo("shed");
        await Assert.That(entries[0].AttemptCount).IsEqualTo(0);
        await Assert.That(entries[0].Exception is WorkRejectedException).IsTrue();
    }

    /// <summary>
    /// Verifies that accepted enqueues never touch the dead-letter pathway.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// All enqueues are admitted, because they stay below capacity and below the watermarks.
    /// </remarks>
    [Test]
    public async Task EnqueueAccepted_NeverRoutesToDlq()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 4;
            opts.WorkerCount = 1;
            opts.DispatchStrategy = DispatchStrategy.PriorityRelaxed;
            opts.FullMode = QueueFullMode.Reject;
        })
        .WithDeadLetterQueue()
        .Build();

        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var dlq = provider.GetRequiredService<IDeadLetterQueue<string>>();

        var first = await orchestrator.EnqueueAsync("a", WorkClass.Interactive);
        var second = await orchestrator.EnqueueAsync("b", WorkClass.Interactive);

        await Assert.That(first).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(second).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(dlq.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that <see cref="RejectionReason.Shutdown"/> rejections during
    /// teardown are NOT dead-lettered (the orchestrator is going away; routing
    /// teardown noise into the DLQ would pollute replay) but ARE still counted —
    /// rejections are always counted, tagged by reason.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// The routing decorator has a real DLQ, and the orchestrator is drained. An enqueue after the
    /// drain rejects with Shutdown. Shutdown must surface and be counted, but it must not
    /// dead-letter.
    /// </remarks>
    [Test]
    public async Task EnqueueRejected_ShutdownReason_DoesNotDeadLetter_ButIsCounted()
    {
        var options = Options.Create(new WorkOrchestratorOptions
        {
            Capacity = 4,
            WorkerCount = 0,
            DispatchStrategy = DispatchStrategy.PriorityRelaxed,
            FullMode = QueueFullMode.Reject,
        });
        await using var orchestrator = new WorkOrchestrator<string>(
            Substitute.For<IWorkHandler<string>>(), options, NullLogger<WorkOrchestrator<string>>.Instance);
        var dlq = new DeadLetterQueue<string>(
            Options.Create(new DeadLetterQueueOptions()), NullLogger<DeadLetterQueue<string>>.Instance);
        var notifier = new DeadLetterNotifier<string>(NullLogger<DeadLetterNotifier<string>>.Instance);
        var routing = new RejectionRoutingOrchestrator<string>(
            orchestrator, dlq, notifier, NullLogger<RejectionRoutingOrchestrator<string>>.Instance);

        var observed = new List<(WorkClass Class, RejectionReason Reason)>();
        routing.RejectionObserved = (workClass, reason) => observed.Add((workClass, reason));

        await orchestrator.DrainAsync();

        var result = await routing.EnqueueAsync("late-item", WorkClass.Default);

        await Assert.That(result.IsAccepted).IsFalse();
        await Assert.That(result.Reason).IsEqualTo(RejectionReason.Shutdown);
        await Assert.That(dlq.Count).IsEqualTo(0);
        await Assert.That(observed.Count).IsEqualTo(1);
        await Assert.That(observed[0].Reason).IsEqualTo(RejectionReason.Shutdown);
    }

    /// <summary>
    /// Verifies that the <c>bifrost.orchestrator.rejected</c> counter increments
    /// with <c>work.class</c> and <c>rejection.reason</c> tags when an enqueue is
    /// rejected.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// The capacity is 1, so the first Interactive item is admitted and the second is rejected. The
    /// instrument identity (name and unit) is part of the DR-3 evidence contract. The counter must
    /// have exactly one increment, tagged by class and reason.
    /// </remarks>
    [Test]
    public async Task RejectedCounter_TaggedByClassAndReason()
    {
        var options = Options.Create(new WorkOrchestratorOptions
        {
            Capacity = 1,
            WorkerCount = 0,
            DispatchStrategy = DispatchStrategy.PriorityRelaxed,
            FullMode = QueueFullMode.Reject,
        });
        await using var orchestrator = new WorkOrchestrator<string>(
            Substitute.For<IWorkHandler<string>>(), options, NullLogger<WorkOrchestrator<string>>.Instance);
        using var metrics = new OrchestratorMetrics<string>(() => orchestrator);
        var routing = new RejectionRoutingOrchestrator<string>(
            orchestrator,
            deadLetterQueue: null,
            notifier: null,
            NullLogger<RejectionRoutingOrchestrator<string>>.Instance)
        {
            RejectionObserved = metrics.RecordRejected,
        };

        using var collector = new RejectedCounterCollector(metrics.Rejected);

        await Assert.That(metrics.Rejected.Name).IsEqualTo("bifrost.orchestrator.rejected");
        await Assert.That(metrics.Rejected.Unit).IsEqualTo("{item}");

        var accepted = await routing.EnqueueAsync("keep", WorkClass.Interactive);
        var rejected = await routing.EnqueueAsync("shed", WorkClass.Interactive);

        await Assert.That(accepted).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(rejected.IsAccepted).IsFalse();

        var measurements = collector.Snapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Value).IsEqualTo(1L);
        await Assert.That(measurements[0].WorkClassTag).IsEqualTo("Interactive");
        await Assert.That(measurements[0].ReasonTag).IsEqualTo("CapacityExceeded");
    }

    /// <summary>
    /// Verifies that keyed-dispatch <see cref="RejectionReason.KeyLimitExceeded"/> rejections are
    /// counted under their own <c>rejection.reason</c> tag and are not dead-lettered, while a
    /// capacity rejection from the same orchestrator still is.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// The keyed queue is injected through the internal factory constructor with a global capacity
    /// of 2 and one pending item per key. <c>A:2</c> and <c>A:3</c> hit the per-key limit on the
    /// synchronous and the asynchronous surface. <c>C:1</c> hits the global limit.
    /// </remarks>
    [Test]
    public async Task KeyLimitRejection_IsCountedWithOwnTag_AndNotDeadLettered()
    {
        await using var orchestrator = new WorkOrchestrator<string>(
            Substitute.For<IWorkHandler<string>>(),
            Options.Create(new WorkOrchestratorOptions { Capacity = 2, WorkerCount = 0, FullMode = QueueFullMode.Reject }),
            NullLogger<WorkOrchestrator<string>>.Instance,
            null,
            null,
            static (opts, _) => KeyedWorkQueue.Create<string, string>(static work => work.Split(':')[0], opts.Capacity, 1, 1));
        using var metrics = new OrchestratorMetrics<string>(() => orchestrator);
        var dlq = new DeadLetterQueue<string>(
            Options.Create(new DeadLetterQueueOptions()), NullLogger<DeadLetterQueue<string>>.Instance);
        var notifier = new DeadLetterNotifier<string>(NullLogger<DeadLetterNotifier<string>>.Instance);
        var routing = new RejectionRoutingOrchestrator<string>(
            orchestrator, dlq, notifier, NullLogger<RejectionRoutingOrchestrator<string>>.Instance)
        {
            RejectionObserved = metrics.RecordRejected,
        };
        using var collector = new RejectedCounterCollector(metrics.Rejected);

        await Assert.That(routing.Enqueue("A:1").IsAccepted).IsTrue();
        await Assert.That(routing.Enqueue("A:2").Reason).IsEqualTo(RejectionReason.KeyLimitExceeded);
        await Assert.That((await routing.EnqueueAsync("A:3")).Reason).IsEqualTo(RejectionReason.KeyLimitExceeded);
        await Assert.That(routing.Enqueue("B:1").IsAccepted).IsTrue();
        await Assert.That((await routing.EnqueueAsync("C:1")).Reason).IsEqualTo(RejectionReason.CapacityExceeded);
        await routing.DrainAsync();

        var measurements = collector.Snapshot();
        await Assert.That(string.Join(",", measurements.Select(static m => m.ReasonTag)))
            .IsEqualTo("KeyLimitExceeded,KeyLimitExceeded,CapacityExceeded");
        var entries = new List<DeadLetteredWork<string>>();
        await foreach (var entry in dlq.ReadAllAsync())
        {
            entries.Add(entry);
        }

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Work).IsEqualTo("C:1");
        await Assert.That((entries[0].Exception as WorkRejectedException)?.Reason).IsEqualTo(RejectionReason.CapacityExceeded);
    }

    /// <summary>
    /// Verifies that accepted enqueues never increment the rejected counter.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Both items are admitted, so there are zero measurements.
    /// </remarks>
    [Test]
    public async Task RejectedCounter_NotIncrementedOnAccepted()
    {
        var options = Options.Create(new WorkOrchestratorOptions
        {
            Capacity = 4,
            WorkerCount = 0,
            DispatchStrategy = DispatchStrategy.PriorityRelaxed,
            FullMode = QueueFullMode.Reject,
        });
        await using var orchestrator = new WorkOrchestrator<string>(
            Substitute.For<IWorkHandler<string>>(), options, NullLogger<WorkOrchestrator<string>>.Instance);
        using var metrics = new OrchestratorMetrics<string>(() => orchestrator);
        var routing = new RejectionRoutingOrchestrator<string>(
            orchestrator,
            deadLetterQueue: null,
            notifier: null,
            NullLogger<RejectionRoutingOrchestrator<string>>.Instance)
        {
            RejectionObserved = metrics.RecordRejected,
        };

        using var collector = new RejectedCounterCollector(metrics.Rejected);

        var first = await routing.EnqueueAsync("a", WorkClass.Interactive);
        var second = await routing.EnqueueAsync("b", WorkClass.Interactive);

        await Assert.That(first).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(second).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(collector.Snapshot().Count).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that <c>WithDeadLetterQueue</c> registers the rejection-routing
    /// decorator wrapping the concrete orchestrator (DR-6 wiring).
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// The routing decorator wraps the bare orchestrator. Without OpenTelemetry, no counter hook is
    /// attached.
    /// </remarks>
    [Test]
    public async Task WithDeadLetterQueue_RegistersRejectionRoutingDecorator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        services.AddWorkOrchestrator<string>()
            .WithDeadLetterQueue()
            .Build();

        await using var provider = services.BuildServiceProvider();

        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        var routing = orchestrator as RejectionRoutingOrchestrator<string>;
        await Assert.That(routing).IsNotNull();
        await Assert.That(routing!.Inner is WorkOrchestrator<string>).IsTrue();
        await Assert.That(routing.RejectionObserved).IsNull();
    }

    /// <summary>
    /// Verifies that <c>WithOpenTelemetry</c> alone (no DLQ) attaches the rejected
    /// counter to the rejection-routing decorator — rejections are always counted,
    /// independent of dead-letter configuration.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// The counter hook is attached although no DLQ is configured.
    /// </remarks>
    [Test]
    public async Task WithOpenTelemetry_AttachesRejectedCounterHook()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddWorkOrchestrator<string>()
            .WithOpenTelemetry()
            .Build();

        await using var provider = services.BuildServiceProvider();

        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        var routing = orchestrator as RejectionRoutingOrchestrator<string>;
        await Assert.That(routing).IsNotNull();
        await Assert.That(routing!.RejectionObserved).IsNotNull();

        provider.GetService<OrchestratorMetrics<string>>()?.Dispose();
    }

    /// <summary>
    /// Handler that parks the worker: signals <see cref="Started"/> when invoked,
    /// then awaits <see cref="Release"/> (or cancellation), keeping the queue
    /// state deterministic while a test fills it to capacity.
    /// </summary>
    private sealed class GateHandler : IWorkHandler<string>
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a task that completes when the handler has been entered.</summary>
        public Task Started => _started.Task;

        /// <summary>Releases the parked worker.</summary>
        public void Release() => _gate.TrySetResult();

        /// <inheritdoc/>
        public async ValueTask HandleAsync(string work, CancellationToken ct)
        {
            _started.TrySetResult();
            await _gate.Task.WaitAsync(ct);
        }
    }

    /// <summary>
    /// Collects measurements from a single counter instrument via
    /// <see cref="MeterListener"/>, isolating the test from same-named meters in
    /// parallel tests by enabling events on the exact instrument instance.
    /// </summary>
    private sealed class RejectedCounterCollector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(long Value, string? WorkClassTag, string? ReasonTag)> _measurements = [];
        private readonly object _sync = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="RejectedCounterCollector"/> class.
        /// </summary>
        /// <param name="instrument">The exact instrument instance to observe.</param>
        /// <remarks>
        /// The <c>_listener.Start</c> call is necessary to activate the listener, so that
        /// <c>OnMeasurement</c> fires.
        /// </remarks>
        public RejectedCounterCollector(Instrument instrument)
        {
            _listener.SetMeasurementEventCallback<long>(OnMeasurement);
            _listener.EnableMeasurementEvents(instrument);
            _listener.Start();
        }

        /// <summary>
        /// Takes a snapshot of the measurements recorded so far.
        /// </summary>
        /// <returns>
        /// The recorded values with their <c>work.class</c> and
        /// <c>rejection.reason</c> tags, in record order.
        /// </returns>
        public IReadOnlyList<(long Value, string? WorkClassTag, string? ReasonTag)> Snapshot()
        {
            lock (_sync)
            {
                return [.. _measurements];
            }
        }

        /// <inheritdoc/>
        public void Dispose() => _listener.Dispose();

        private void OnMeasurement(
            Instrument instrument,
            long value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state)
        {
            string? workClassTag = null;
            string? reasonTag = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "work.class")
                {
                    workClassTag = tag.Value as string;
                }
                else if (tag.Key == "rejection.reason")
                {
                    reasonTag = tag.Value as string;
                }
            }

            lock (_sync)
            {
                _measurements.Add((value, workClassTag, reasonTag));
            }
        }
    }
}
