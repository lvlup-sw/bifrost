// =============================================================================
// <copyright file="QueueWaitMetricsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.Metrics;

using Bifrost.Core;
using Bifrost.DependencyInjection;
using Bifrost.OpenTelemetry;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.OpenTelemetry;

/// <summary>
/// Tests for the <c>bifrost.orchestrator.queue_wait</c> histogram (T24): queue wait
/// recorded at dequeue, tagged by <see cref="WorkClass"/>, computed from
/// <see cref="TimeProvider"/> monotonic elapsed time — never wall clock. This
/// instrument is the Stage-2 evidence signal for priority dispatch (issue #17).
/// </summary>
[Property("Category", "Unit")]
public class QueueWaitMetricsTests
{
    /// <summary>
    /// Per-test ceiling for waits that are expected to complete promptly.
    /// </summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Verifies that dequeueing an envelope records exactly one
    /// <c>bifrost.orchestrator.queue_wait</c> measurement with the fake-clock elapsed
    /// value in milliseconds and a <c>work.class</c> tag carrying the enum name.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// <see cref="FakeTimeProvider"/> makes the queue-wait duration deterministic. <c>WorkerCount = 0</c>
    /// defers the dequeue until the dynamic worker starts. The instrument name and unit are part of the
    /// evidence-recipe contract. The test expects one measurement of 250 ms with the tag
    /// <c>work.class="Batch"</c>.
    /// </remarks>
    [Test]
    public async Task QueueWait_RecordedAtDequeue_TaggedByClass()
    {
        var fakeTime = new FakeTimeProvider();
        var handler = new CountingHandler(expected: 1);
        var options = Options.Create(new WorkOrchestratorOptions { WorkerCount = 0 });
        await using var orchestrator = new WorkOrchestrator<string>(
            handler, options, NullLogger<WorkOrchestrator<string>>.Instance, fakeTime);

        using var metrics = new OrchestratorMetrics<string>(() => orchestrator);
        orchestrator.QueueWaitObserved = metrics.RecordQueueWait;
        using var collector = new QueueWaitCollector(metrics.QueueWait);

        await Assert.That(metrics.QueueWait.Name).IsEqualTo("bifrost.orchestrator.queue_wait");
        await Assert.That(metrics.QueueWait.Unit).IsEqualTo("ms");

        var enqueued = await orchestrator.EnqueueAsync("batch-item", WorkClass.Batch).ConfigureAwait(false);
        await Assert.That(enqueued).IsEqualTo(EnqueueResult.Accepted);
        fakeTime.Advance(TimeSpan.FromMilliseconds(250));

        var workerFunc = orchestrator.CreateWorkerFunction();
        using var cts = new CancellationTokenSource();
        var workerTask = Task.Run(() => workerFunc("MetricsWorker", cts.Token), cts.Token);
        await handler.Done.WaitAsync(WaitTimeout).ConfigureAwait(false);

        var measurements = collector.Snapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Value).IsEqualTo(250.0);
        await Assert.That(measurements[0].WorkClassTag).IsEqualTo("Batch");

        await cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(workerTask, Task.Delay(WaitTimeout)).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that the recorded value is the <see cref="TimeProvider"/> monotonic
    /// elapsed time: with the fake clock frozen, real wall-clock time passing between
    /// enqueue and dequeue must not leak into the measurement.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// The fake clock stays frozen while 50 ms of real time pass. Any nonzero measurement is wall-clock
    /// leakage, so the measurement must be exactly zero.
    /// </remarks>
    [Test]
    public async Task QueueWait_UsesTimeProviderTicks_NotWallClock()
    {
        var fakeTime = new FakeTimeProvider();
        var handler = new CountingHandler(expected: 1);
        var options = Options.Create(new WorkOrchestratorOptions { WorkerCount = 0 });
        await using var orchestrator = new WorkOrchestrator<string>(
            handler, options, NullLogger<WorkOrchestrator<string>>.Instance, fakeTime);

        using var metrics = new OrchestratorMetrics<string>(() => orchestrator);
        orchestrator.QueueWaitObserved = metrics.RecordQueueWait;
        using var collector = new QueueWaitCollector(metrics.QueueWait);

        var enqueued = await orchestrator.EnqueueAsync("frozen-item").ConfigureAwait(false);
        await Assert.That(enqueued).IsEqualTo(EnqueueResult.Accepted);

        await Task.Delay(50).ConfigureAwait(false);

        var workerFunc = orchestrator.CreateWorkerFunction();
        using var cts = new CancellationTokenSource();
        var workerTask = Task.Run(() => workerFunc("FrozenWorker", cts.Token), cts.Token);
        await handler.Done.WaitAsync(WaitTimeout).ConfigureAwait(false);

        var measurements = collector.Snapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Value).IsEqualTo(0.0);

        await cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(workerTask, Task.Delay(WaitTimeout)).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that interleaved Interactive/Default/Batch items produce separately
    /// tagged measurements with each envelope's own queue wait.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// The test staggers the enqueues on the fake clock. Each class then has a distinct expected wait when
    /// the worker drains the FIFO queue at T0 + 300 ms.
    /// </remarks>
    [Test]
    public async Task QueueWait_MultipleClasses_TaggedSeparately()
    {
        var fakeTime = new FakeTimeProvider();
        var handler = new CountingHandler(expected: 3);
        var options = Options.Create(new WorkOrchestratorOptions { WorkerCount = 0 });
        await using var orchestrator = new WorkOrchestrator<string>(
            handler, options, NullLogger<WorkOrchestrator<string>>.Instance, fakeTime);

        using var metrics = new OrchestratorMetrics<string>(() => orchestrator);
        orchestrator.QueueWaitObserved = metrics.RecordQueueWait;
        using var collector = new QueueWaitCollector(metrics.QueueWait);

        await orchestrator.EnqueueAsync("interactive-item", WorkClass.Interactive).ConfigureAwait(false);
        fakeTime.Advance(TimeSpan.FromMilliseconds(100));
        await orchestrator.EnqueueAsync("default-item", WorkClass.Default).ConfigureAwait(false);
        fakeTime.Advance(TimeSpan.FromMilliseconds(100));
        await orchestrator.EnqueueAsync("batch-item", WorkClass.Batch).ConfigureAwait(false);
        fakeTime.Advance(TimeSpan.FromMilliseconds(100));

        var workerFunc = orchestrator.CreateWorkerFunction();
        using var cts = new CancellationTokenSource();
        var workerTask = Task.Run(() => workerFunc("MixedWorker", cts.Token), cts.Token);
        await handler.Done.WaitAsync(WaitTimeout).ConfigureAwait(false);

        var measurements = collector.Snapshot();
        await Assert.That(measurements.Count).IsEqualTo(3);

        var interactive = measurements.Where(m => m.WorkClassTag == "Interactive").ToList();
        var defaults = measurements.Where(m => m.WorkClassTag == "Default").ToList();
        var batch = measurements.Where(m => m.WorkClassTag == "Batch").ToList();
        await Assert.That(interactive.Count).IsEqualTo(1);
        await Assert.That(defaults.Count).IsEqualTo(1);
        await Assert.That(batch.Count).IsEqualTo(1);
        await Assert.That(interactive[0].Value).IsEqualTo(300.0);
        await Assert.That(defaults[0].Value).IsEqualTo(200.0);
        await Assert.That(batch[0].Value).IsEqualTo(100.0);

        await cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(workerTask, Task.Delay(WaitTimeout)).ConfigureAwait(false);
    }

    /// <summary>
    /// Regression pin: the pre-existing <see cref="OrchestratorMetrics{TWork}"/>
    /// instruments keep their names and units unchanged when the queue-wait histogram
    /// is added.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    [Test]
    public async Task ExistingMetrics_DepthDurationWorkers_Unchanged()
    {
        using var metrics = new OrchestratorMetrics<string>(() => null, () => 0);

        await Assert.That(metrics.ItemsEnqueued.Meter.Name).IsEqualTo("Bifrost.String");
        await Assert.That(metrics.ItemsEnqueued.Name).IsEqualTo("orchestrator.items.enqueued");
        await Assert.That(metrics.ItemsEnqueued.Unit).IsEqualTo("{item}");
        await Assert.That(metrics.ItemsProcessed.Name).IsEqualTo("orchestrator.items.processed");
        await Assert.That(metrics.ItemsProcessed.Unit).IsEqualTo("{item}");
        await Assert.That(metrics.ItemsFailed.Name).IsEqualTo("orchestrator.items.failed");
        await Assert.That(metrics.ItemsFailed.Unit).IsEqualTo("{item}");
        await Assert.That(metrics.ItemsDeadLettered.Name).IsEqualTo("orchestrator.items.deadlettered");
        await Assert.That(metrics.ItemsDeadLettered.Unit).IsEqualTo("{item}");
        await Assert.That(metrics.ProcessingDuration.Name).IsEqualTo("orchestrator.processing.duration");
        await Assert.That(metrics.ProcessingDuration.Unit).IsEqualTo("ms");
        await Assert.That(metrics.PendingItems.Name).IsEqualTo("orchestrator.queue.pending");
        await Assert.That(metrics.PendingItems.Unit).IsEqualTo("{item}");
        await Assert.That(metrics.ActiveWorkers.Name).IsEqualTo("orchestrator.workers.active");
        await Assert.That(metrics.ActiveWorkers.Unit).IsEqualTo("{worker}");
        await Assert.That(metrics.DeadLetterQueueDepth!.Name).IsEqualTo("orchestrator.dlq.depth");
        await Assert.That(metrics.DeadLetterQueueDepth!.Unit).IsEqualTo("{item}");
    }

    /// <summary>
    /// Verifies that <see cref="OpenTelemetryExtensions.WithOpenTelemetry{TWork}"/>
    /// attaches the queue-wait hook to the concrete <see cref="WorkOrchestrator{TWork}"/>
    /// when the orchestrator is built through DI.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// <c>WithOpenTelemetry</c> makes sure that the rejection-routing decorator wraps the concrete
    /// orchestrator (T23, DR-6: rejections are always counted). The queue-wait hook still attaches to the
    /// bare <c>WorkOrchestrator</c> beneath it, through the pass-through factory at the innermost order.
    /// </remarks>
    [Test]
    public async Task WithOpenTelemetry_AttachesQueueWaitHook()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkHandler<string>>(Substitute.For<IWorkHandler<string>>());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        var builder = services.AddWorkOrchestrator<string>();

        builder.WithOpenTelemetry();
        builder.Build();
        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        var routing = orchestrator as Bifrost.Decorators.RejectionRoutingOrchestrator<string>;
        await Assert.That(routing).IsNotNull();
        var concrete = routing!.Inner as WorkOrchestrator<string>;
        await Assert.That(concrete).IsNotNull();
        await Assert.That(concrete!.QueueWaitObserved).IsNotNull();

        if (orchestrator is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }

        provider.GetService<OrchestratorMetrics<string>>()?.Dispose();
    }

    /// <summary>
    /// Collects measurements from a single histogram instrument via
    /// <see cref="MeterListener"/>, isolating the test from same-named meters in
    /// parallel tests by enabling events on the exact instrument instance.
    /// </summary>
    private sealed class QueueWaitCollector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(double Value, string? WorkClassTag)> _measurements = [];
        private readonly object _sync = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="QueueWaitCollector"/> class.
        /// </summary>
        /// <param name="instrument">The exact instrument instance to observe.</param>
        /// <remarks>
        /// The constructor must call <c>Start</c> on the listener. Without this call, <c>OnMeasurement</c>
        /// does not fire.
        /// </remarks>
        public QueueWaitCollector(Instrument instrument)
        {
            _listener.SetMeasurementEventCallback<double>(OnMeasurement);
            _listener.EnableMeasurementEvents(instrument);
            _listener.Start();
        }

        /// <summary>
        /// Takes a snapshot of the measurements recorded so far.
        /// </summary>
        /// <returns>The recorded values with their <c>work.class</c> tag, in record order.</returns>
        public IReadOnlyList<(double Value, string? WorkClassTag)> Snapshot()
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
            double value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state)
        {
            string? workClassTag = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "work.class")
                {
                    workClassTag = tag.Value as string;
                }
            }

            lock (_sync)
            {
                _measurements.Add((value, workClassTag));
            }
        }
    }

    /// <summary>
    /// Handler that completes <see cref="Done"/> once the expected number of items has
    /// been handled.
    /// </summary>
    private sealed class CountingHandler : IWorkHandler<string>
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _expected;
        private int _count;

        /// <summary>
        /// Initializes a new instance of the <see cref="CountingHandler"/> class.
        /// </summary>
        /// <param name="expected">The number of handled items that completes <see cref="Done"/>.</param>
        public CountingHandler(int expected) => _expected = expected;

        /// <summary>Gets a task that completes when the expected item count is reached.</summary>
        public Task Done => _done.Task;

        /// <inheritdoc/>
        public ValueTask HandleAsync(string work, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _count) >= _expected)
            {
                _done.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }
}
