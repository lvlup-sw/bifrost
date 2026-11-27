// =============================================================================
// <copyright file="AdmissionRejectionFloodTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.DependencyInjection;
using Bifrost.HealthChecks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using TUnit.Core;

namespace Bifrost.Tests.DeadLetter;

/// <summary>
/// Admission rejections must not be able to erase handler failures from the dead-letter queue
/// or hold the dead-letter health check at <see cref="HealthStatus.Unhealthy"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>WithDeadLetterQueue</c> installs <c>RejectionRoutingOrchestrator</c> with no opt-out.
/// That decorator writes every <see cref="RejectionReason.CapacityExceeded"/> and
/// <see cref="RejectionReason.WatermarkExceeded"/> result into the same bounded buffer that
/// stores handler failures. The buffer drops its oldest entry at capacity. These tests
/// admit one real handler failure, fill the work queue, and then reject a burst on each
/// public enqueue surface. The handler failure must still be readable, and a buffer that
/// holds only shed work must not report unhealthy.
/// </para>
/// <para>
/// <see cref="IWorkWriter{TWork}.EnqueueAsync"/> on a FIFO queue in
/// <see cref="QueueFullMode.Wait"/> waits instead of rejecting, so it is not a flood surface.
/// <see cref="RejectionReason.KeyLimitExceeded"/> and <see cref="RejectionReason.Shutdown"/>
/// are not routed. <see cref="IWorkWriter{TWork}.TryEnqueue"/> forwards to
/// <see cref="IWorkWriter{TWork}.Enqueue"/>, so watermark and keyed calls on that surface are
/// the same routing branch as the synchronous tests.
/// </para>
/// </remarks>
[Property("Category", "Unit")]
public class AdmissionRejectionFloodTests
{
    private const string HandlerFailure = "handler-failure";

    /// <summary>
    /// A default FIFO orchestrator rejects synchronous enqueues once the work queue is full.
    /// A burst the size of the default dead-letter capacity must not drop the handler failure
    /// or make the dead-letter health check unhealthy.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// Worker count and full mode stay at the defaults (one worker is set only so the failure
    /// can be processed, and full mode stays <see cref="QueueFullMode.Wait"/>). The dead-letter
    /// capacity stays at its default of 1000, which is also the health check's unhealthy line.
    /// </remarks>
    [Test]
    public async Task SyncEnqueue_OnFullFifoQueue_KeepsHandlerFailuresAndLeavesHealthHealthy()
    {
        var capacity = new DeadLetterQueueOptions().Capacity;
        await using var host = await FloodHost.StartAsync(workCapacity: 1, fullMode: QueueFullMode.Wait, deadLetterCapacity: null, configure: null);

        await host.CaptureHandlerFailureAsync();
        await host.FillAsync(host.Orchestrator.Capacity, WorkClass.Interactive);

        var rejected = 0;
        for (var i = 0; i < capacity; i++)
        {
            var result = host.Orchestrator.Enqueue($"shed-{i}");
            if (result.Reason == RejectionReason.CapacityExceeded)
            {
                rejected++;
            }
        }

        await Assert.That(rejected).IsEqualTo(capacity);
        await host.SettleRoutesAsync();

        var report = await host.Health.CheckHealthAsync();
        var entries = await host.ReadDeadLettersAsync();
        await Assert.That(ContainsHandlerFailure(entries)).IsTrue();
        await Assert.That(report.Status).IsEqualTo(HealthStatus.Healthy);
    }

    /// <summary>
    /// Boolean <see cref="IWorkWriter{TWork}.TryEnqueue"/> on a full FIFO queue must not
    /// drop a handler failure out of the dead-letter queue.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TryEnqueue_OnFullFifoQueue_KeepsHandlerFailures()
    {
        await using var host = await FloodHost.StartAsync(workCapacity: 1, fullMode: QueueFullMode.Wait, deadLetterCapacity: 4, configure: null);
        await host.CaptureHandlerFailureAsync();
        await host.FillAsync(host.Orchestrator.Capacity, WorkClass.Interactive);

        var rejected = 0;
        for (var i = 0; i < 8; i++)
        {
            if (!host.Orchestrator.TryEnqueue($"shed-{i}"))
            {
                rejected++;
            }
        }

        await Assert.That(rejected).IsEqualTo(8);
        await host.SettleRoutesAsync();
        await Assert.That(ContainsHandlerFailure(await host.ReadDeadLettersAsync())).IsTrue();
    }

    /// <summary>
    /// <see cref="IWorkWriter{TWork}.EnqueueAsync"/> in <see cref="QueueFullMode.Reject"/>
    /// must not drop a handler failure when the FIFO queue is full.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task EnqueueAsync_WhenFullModeRejects_KeepsHandlerFailures()
    {
        await using var host = await FloodHost.StartAsync(
            workCapacity: 1,
            fullMode: QueueFullMode.Reject,
            deadLetterCapacity: 4,
            configure: null);

        await host.CaptureHandlerFailureAsync();
        await host.FillAsync(host.Orchestrator.Capacity, WorkClass.Interactive);
        await RejectManyAsync(host, 8, static (orchestrator, work) => orchestrator.EnqueueAsync(work), RejectionReason.CapacityExceeded);
        await host.SettleRoutesAsync();
        await Assert.That(ContainsHandlerFailure(await host.ReadDeadLettersAsync())).IsTrue();
    }

    /// <summary>
    /// Relaxed priority dispatch rejects batch work at the watermark. Those rejections must
    /// not drop a handler failure.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// The batch watermark is one half of a capacity of four, so two queued interactive items
    /// put batch work into <see cref="RejectionReason.WatermarkExceeded"/> while interactive
    /// work is still admitted.
    /// </remarks>
    [Test]
    public async Task EnqueueAsync_WhenPriorityWatermarkRejects_KeepsHandlerFailures()
    {
        await using var host = await FloodHost.StartAsync(
            workCapacity: 4,
            fullMode: QueueFullMode.Wait,
            deadLetterCapacity: 4,
            configure: static builder => builder.UsePriorityDispatch(
                DispatchStrategy.PriorityRelaxed,
                static priority =>
                {
                    priority.BatchAdmissionWatermark = 0.5;
                    priority.DefaultAdmissionWatermark = 0.75;
                    priority.InteractiveAdmissionWatermark = 1.0;
                }));

        await host.CaptureHandlerFailureAsync();
        await host.FillAsync(2, WorkClass.Interactive);
        await RejectManyAsync(
            host,
            8,
            static (orchestrator, work) => orchestrator.EnqueueAsync(work, WorkClass.Batch),
            RejectionReason.WatermarkExceeded);
        await host.SettleRoutesAsync();
        await Assert.That(ContainsHandlerFailure(await host.ReadDeadLettersAsync())).IsTrue();
    }

    /// <summary>
    /// Exact priority dispatch rejects batch work at the watermark on the synchronous surface.
    /// Those rejections must not drop a handler failure.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Enqueue_WhenExactPriorityWatermarkRejects_KeepsHandlerFailures()
    {
        await using var host = await FloodHost.StartAsync(
            workCapacity: 4,
            fullMode: QueueFullMode.Wait,
            deadLetterCapacity: 4,
            configure: static builder => builder.UsePriorityDispatch(
                DispatchStrategy.PriorityExact,
                static priority =>
                {
                    priority.BatchAdmissionWatermark = 0.5;
                    priority.DefaultAdmissionWatermark = 0.75;
                    priority.InteractiveAdmissionWatermark = 1.0;
                }));

        await host.CaptureHandlerFailureAsync();
        await host.FillAsync(2, WorkClass.Interactive);

        var rejected = 0;
        for (var i = 0; i < 8; i++)
        {
            if (host.Orchestrator.Enqueue($"shed-{i}", WorkClass.Batch).Reason == RejectionReason.WatermarkExceeded)
            {
                rejected++;
            }
        }

        await Assert.That(rejected).IsEqualTo(8);
        await host.SettleRoutesAsync();
        await Assert.That(ContainsHandlerFailure(await host.ReadDeadLettersAsync())).IsTrue();
    }

    /// <summary>
    /// Interactive work on a relaxed priority queue rejects at hard capacity. Those rejections
    /// must not drop a handler failure.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// The interactive watermark stays at 1, so the rejection reason is
    /// <see cref="RejectionReason.CapacityExceeded"/> rather than the watermark.
    /// </remarks>
    [Test]
    public async Task EnqueueAsync_WhenPriorityQueueIsAtCapacity_KeepsHandlerFailures()
    {
        await using var host = await FloodHost.StartAsync(
            workCapacity: 2,
            fullMode: QueueFullMode.Wait,
            deadLetterCapacity: 4,
            configure: static builder => builder.UsePriorityDispatch(DispatchStrategy.PriorityRelaxed));

        await host.CaptureHandlerFailureAsync();
        await host.FillAsync(host.Orchestrator.Capacity, WorkClass.Interactive);
        await RejectManyAsync(
            host,
            8,
            static (orchestrator, work) => orchestrator.EnqueueAsync(work, WorkClass.Interactive),
            RejectionReason.CapacityExceeded);
        await host.SettleRoutesAsync();
        await Assert.That(ContainsHandlerFailure(await host.ReadDeadLettersAsync())).IsTrue();
    }

    /// <summary>
    /// Keyed dispatch rejects at the global capacity. Those rejections must not drop a
    /// handler failure. Per-key limit rejections are not part of this path.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task EnqueueAsync_WhenKeyedQueueIsAtCapacity_KeepsHandlerFailures()
    {
        await using var host = await FloodHost.StartAsync(
            workCapacity: 2,
            fullMode: QueueFullMode.Wait,
            deadLetterCapacity: 4,
            configure: static builder => builder.WithKeyedDispatch<string, string>(static work => work, 100, 1));

        await host.CaptureHandlerFailureAsync();
        await host.FillAsync(host.Orchestrator.Capacity, WorkClass.Default);
        await RejectManyAsync(
            host,
            8,
            static (orchestrator, work) => orchestrator.EnqueueAsync(work),
            RejectionReason.CapacityExceeded);
        await host.SettleRoutesAsync();
        await Assert.That(ContainsHandlerFailure(await host.ReadDeadLettersAsync())).IsTrue();
    }

    /// <summary>
    /// A dead-letter queue filled only by admission rejections must stay healthy, and the same
    /// check must still report unhealthy when it holds a thousand handler failures.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// The burst uses the default dead-letter capacity and the synchronous FIFO enqueue surface.
    /// Nothing reads the dead-letter queue between the burst and the health check. The second
    /// half writes handler-failure entries through <see cref="IDeadLetterQueue{TWork}.EnqueueAsync"/>,
    /// the same method the terminal outcome stage uses, and expects the published unhealthy line
    /// of 1000.
    /// </remarks>
    [Test]
    public async Task AdmissionRejectionBurst_DoesNotReportDeadLetterUnhealthy()
    {
        var capacity = new DeadLetterQueueOptions().Capacity;
        await using var host = await FloodHost.StartAsync(workCapacity: 1, fullMode: QueueFullMode.Wait, deadLetterCapacity: null, configure: null);

        await host.FillAsync(host.Orchestrator.Capacity, WorkClass.Interactive);

        var rejected = 0;
        for (var i = 0; i < capacity; i++)
        {
            if (host.Orchestrator.Enqueue($"shed-{i}").Reason == RejectionReason.CapacityExceeded)
            {
                rejected++;
            }
        }

        await Assert.That(rejected).IsEqualTo(capacity);
        await host.SettleRoutesAsync();

        var shed = await host.Health.CheckHealthAsync();
        await Assert.That(shed.Status).IsEqualTo(HealthStatus.Healthy);

        await host.ReadDeadLettersAsync();
        for (var i = 0; i < capacity; i++)
        {
            await host.DeadLetters.EnqueueAsync(new DeadLetteredWork<string>(
                $"failed-{i}",
                new InvalidOperationException("handler failed"),
                AttemptCount: 1,
                DateTimeOffset.UtcNow,
                CorrelationId: null));
        }

        var failures = await host.Health.CheckHealthAsync();
        await Assert.That(failures.Status).IsEqualTo(HealthStatus.Unhealthy);
    }

    private static bool ContainsHandlerFailure(List<DeadLetteredWork<string>> entries)
        => entries.Exists(static entry =>
            entry.Work == HandlerFailure
            && entry.AttemptCount >= 1
            && entry.Exception is not WorkRejectedException);

    private static async Task RejectManyAsync(
        FloodHost host,
        int count,
        Func<IWorkOrchestrator<string>, string, ValueTask<EnqueueResult>> enqueue,
        RejectionReason reason)
    {
        var rejected = 0;
        for (var i = 0; i < count; i++)
        {
            var result = await enqueue(host.Orchestrator, $"shed-{i}").AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (result.Reason == reason)
            {
                rejected++;
            }
        }

        await Assert.That(rejected).IsEqualTo(count);
    }

    /// <summary>
    /// One orchestrator with a dead-letter queue, a single worker, and the dead-letter health check.
    /// </summary>
    private sealed class FloodHost : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ParkGate _gate;
        private int _settled;

        private FloodHost(ServiceProvider provider, ParkGate gate, IWorkOrchestrator<string> orchestrator, IDeadLetterQueue<string> deadLetters, HealthCheckService health)
        {
            _provider = provider;
            _gate = gate;
            Orchestrator = orchestrator;
            DeadLetters = deadLetters;
            Health = health;
        }

        public IWorkOrchestrator<string> Orchestrator { get; }

        public IDeadLetterQueue<string> DeadLetters { get; }

        public HealthCheckService Health { get; }

        public static Task<FloodHost> StartAsync(
            int workCapacity,
            QueueFullMode fullMode,
            int? deadLetterCapacity,
            Action<WorkOrchestratorBuilder<string>>? configure)
        {
            var gate = new ParkGate();
            var services = new ServiceCollection();
            services.AddLogging();
            var builder = services.AddWorkOrchestrator<string>(options =>
            {
                options.Capacity = workCapacity;
                options.WorkerCount = 1;
                options.FullMode = fullMode;
            });
            builder.WithHandler(gate.HandleAsync);
            configure?.Invoke(builder);
            builder.WithDeadLetterQueue(options =>
            {
                options.MaxRetries = 0;
                if (deadLetterCapacity is int capacity)
                {
                    options.Capacity = capacity;
                }
            });
            builder.Build();
            services.AddHealthChecks().AddDeadLetterQueueHealthCheck<string>();

            var provider = services.BuildServiceProvider();
            return Task.FromResult(new FloodHost(
                provider,
                gate,
                provider.GetRequiredService<IWorkOrchestrator<string>>(),
                provider.GetRequiredService<IDeadLetterQueue<string>>(),
                provider.GetRequiredService<HealthCheckService>()));
        }

        public async Task CaptureHandlerFailureAsync()
        {
            var admitted = await Orchestrator.EnqueueAsync(HandlerFailure);
            await Assert.That(admitted.IsAccepted).IsTrue();
            var deadline = Environment.TickCount64 + 5000;
            while (DeadLetters.Count < 1 && Environment.TickCount64 < deadline)
            {
                await Task.Delay(10);
            }

            await Assert.That(DeadLetters.Count).IsGreaterThanOrEqualTo(1);
        }

        public async Task FillAsync(int queuedItems, WorkClass workClass)
        {
            var parked = Orchestrator.Enqueue("park", WorkClass.Interactive);
            await Assert.That(parked.IsAccepted).IsTrue();
            await _gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < queuedItems; i++)
            {
                var filled = Orchestrator.Enqueue($"fill-{i}", workClass);
                await Assert.That(filled.IsAccepted).IsTrue();
            }
        }

        public async Task SettleRoutesAsync()
        {
            _gate.Release();
            await Orchestrator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            _settled = 1;
        }

        public async Task<List<DeadLetteredWork<string>>> ReadDeadLettersAsync()
        {
            var entries = new List<DeadLetteredWork<string>>();
            await foreach (var entry in DeadLetters.ReadAllAsync())
            {
                entries.Add(entry);
            }

            return entries;
        }

        public async ValueTask DisposeAsync()
        {
            _gate.Release();
            if (_settled == 0)
            {
                await Orchestrator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }

            await _provider.DisposeAsync();
        }
    }

    /// <summary>
    /// Parks the single worker after the handler failure has been captured, so later enqueues
    /// sit in the work queue until it is full.
    /// </summary>
    private sealed class ParkGate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public async ValueTask HandleAsync(string work, CancellationToken cancellationToken)
        {
            if (work == HandlerFailure)
            {
                throw new InvalidOperationException("handler failed");
            }

            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }
    }
}
