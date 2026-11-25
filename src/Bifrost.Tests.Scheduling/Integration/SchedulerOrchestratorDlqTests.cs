// =============================================================================
// <copyright file="SchedulerOrchestratorDlqTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.Core.Events;
using Bifrost.DependencyInjection;
using Bifrost.Resilience;
using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;
using Bifrost.Scheduling.DependencyInjection;
using Bifrost.Scheduling.Observability;
using Bifrost.Scheduling.TickEngine;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.Integration;

/// <summary>
/// Acceptance tests for DR-4: integration across scheduler, orchestrator, resilience,
/// and DLQ (Task 40). The full stack is assembled via the real DI extensions:
/// <c>AddScheduler(…)</c>, <c>AddWorkOrchestrator&lt;TWork&gt;(…)</c>,
/// <c>.WithResilience(…)</c>, and <c>.WithDeadLetterQueue(…)</c>. Fires are driven
/// deterministically by advancing a <see cref="FakeTimeProvider"/> and awaiting the
/// <see cref="ScheduleTickLoop.WaitForIdleAsync"/> quiescence barrier — no real
/// wall-clock waits, no sleep.
/// </summary>
/// <remarks>
/// <para>
/// The scheduler event stream (published by the tick loop and registry) and the
/// work event stream (published by <c>EventStreamOrchestrator</c>) are separate
/// channels. Test 4 verifies both emit on the expected path. Tests 1–3 focus on
/// round-trip processing, resilience+DLQ interaction, and schedule durability across
/// handler failures using simpler polling on the DLQ and handler counters.
/// </para>
/// <para>
/// NOTE: The <c>ISchedulerTestHarness</c> / <c>AddSchedulerTesting()</c> API was
/// not present in this worktree at the time of implementation (the sibling harness
/// agent was in parallel). Tests drive fires directly via
/// <c>ScheduleTickLoop.WaitForIdleAsync</c> (internal, accessible via
/// <c>InternalsVisibleTo</c>) and <see cref="FakeTimeProvider.Advance"/>, which is
/// the same primitive the planned harness wraps.
/// </para>
/// </remarks>
[Property("Category", "Integration")]
public sealed class SchedulerOrchestratorDlqTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 14, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan FireInterval = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    // =========================================================================
    // Test 1: happy path — job fires, work processed by handler
    // =========================================================================

    /// <summary>
    /// Verifies the full scheduling→orchestrator→handler round trip: a scheduled
    /// job fires at the configured interval, the orchestrator job dispatcher
    /// enqueues the work item, and the real work orchestrator delivers it to the
    /// handler.
    /// </summary>
    /// <remarks>
    /// The worker runs on the thread pool, not on the tick thread. So after the tick loop is idle,
    /// the test still waits up to <c>TestTimeout</c> for the handler to process the work.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ScheduledJob_DispatchesToOrchestrator_WorkProcessedByHandler()
    {
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var fx = await Fixture.StartAsync(services =>
        {
            services
                .AddWorkOrchestrator<ScheduledWork>(opts => opts.WorkerCount = 1)
                .WithHandler<ScheduledWork>((work, ct) =>
                {
                    processed.TrySetResult();
                    return ValueTask.CompletedTask;
                })
                .WithResilience(s =>
                {
                    s.RetryCount = 0;
                    s.RetryIntervalSeconds = 0;
                    s.TimeoutIntervalSeconds = 30;
                })
                .WithDeadLetterQueue(opts => opts.MaxRetries = 0)
                .Build();
        }, scheduler =>
            scheduler.AddJob<ScheduledWork>("round-trip")
                .Every(FireInterval)
                .DispatchTo<IWorkOrchestrator<ScheduledWork>>(
                    _ => new ScheduledWork("hello"),
                    WorkClass.Batch));

        fx.Time.Advance(FireInterval);
        await fx.Loop.WaitForIdleAsync(TestTimeout);

        var completed = await Task.WhenAny(processed.Task, Task.Delay(TestTimeout));

        await Assert.That(completed == processed.Task).IsTrue();
    }

    // =========================================================================
    // Test 2: resilience retries then DLQ
    // =========================================================================

    /// <summary>
    /// Verifies that when a handler throws on every attempt, Polly retries (via
    /// <c>WithResilience</c>) do not engage for handler failures (resilience wraps
    /// enqueue, not handler execution), the DLQ handler decorator retries up to
    /// <c>MaxRetries</c>, and the item lands in the dead-letter queue after all
    /// attempts are exhausted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handler always throws. The DLQ allows one retry (<c>MaxRetries</c> = 1), so the handler
    /// runs two times in total: one initial call and one DLQ retry.
    /// </para>
    /// <para>
    /// Resilience retries enqueue failures, not handler failures. So its <c>RetryCount</c> of 5
    /// must not add to the DLQ retry count. The handler is asynchronous and runs on the pool, so
    /// the test polls until the item lands in the DLQ. Exactly 2 handler calls prove that the
    /// resilience retries do not compound with the DLQ retries.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ScheduledJob_HandlerThrows_ResilienceRetries_ThenDeadLetters()
    {
        var handlerCallCount = 0;
        var dlqReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var fx = await Fixture.StartAsync(services =>
        {
            services
                .AddWorkOrchestrator<ScheduledWork>(opts => opts.WorkerCount = 1)
                .WithHandler<ScheduledWork>((work, ct) =>
                {
                    Interlocked.Increment(ref handlerCallCount);
                    return new ValueTask(Task.FromException(
                        new InvalidOperationException("handler always fails")));
                })
                .WithResilience(s =>
                {
                    s.RetryCount = 5;
                    s.RetryIntervalSeconds = 0;
                    s.TimeoutIntervalSeconds = 30;
                })
                .WithDeadLetterQueue(opts => opts.MaxRetries = 1)
                .Build();
        }, scheduler =>
            scheduler.AddJob<ScheduledWork>("failing-job")
                .Every(FireInterval)
                .DispatchTo<IWorkOrchestrator<ScheduledWork>>(
                    _ => new ScheduledWork("fail"),
                    WorkClass.Batch));

        var dlq = fx.Provider.GetRequiredService<IDeadLetterQueue<ScheduledWork>>();

        fx.Time.Advance(FireInterval);
        await fx.Loop.WaitForIdleAsync(TestTimeout);

        using var pollCts = new CancellationTokenSource(TestTimeout);
        while (dlq.Count == 0 && !pollCts.IsCancellationRequested)
        {
            await Task.Delay(20, pollCts.Token);
        }

        await Assert.That(dlq.Count).IsEqualTo(1);
        await Assert.That(handlerCallCount).IsEqualTo(2);

        var entries = new List<DeadLetteredWork<ScheduledWork>>();
        await foreach (var entry in dlq.ReadAllAsync())
        {
            entries.Add(entry);
        }

        await Assert.That(entries).HasCount(1);
        await Assert.That(entries[0].AttemptCount).IsEqualTo(2);
        await Assert.That(entries[0].Exception).IsTypeOf<InvalidOperationException>();
    }

    // =========================================================================
    // Test 3: schedule stays alive after handler failures
    // =========================================================================

    /// <summary>
    /// Verifies the schedule is durable across handler failures: when a job fires
    /// and the handler fails (dead-lettering the work), the scheduler's next
    /// occurrence is still scheduled and fires on its next interval. The tick loop
    /// must not be poisoned by a work-side failure.
    /// </summary>
    /// <remarks>
    /// The handler always throws, and <c>MaxRetries</c> = 0 makes each fire fail at once and
    /// dead-letter one item. The test fires the job one time and waits for the first handler call
    /// to fail and dead-letter. Then it advances one more interval, and the schedule must still be
    /// live. The second handler call proves that the schedule survived the failure.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ScheduledJob_KeepsFiring_EvenAfterHandlerFailures()
    {
        var fireCount = 0;
        var secondFireReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var fx = await Fixture.StartAsync(services =>
        {
            services
                .AddWorkOrchestrator<ScheduledWork>(opts => opts.WorkerCount = 1)
                .WithHandler<ScheduledWork>((work, ct) =>
                {
                    var count = Interlocked.Increment(ref fireCount);
                    if (count >= 2)
                    {
                        secondFireReady.TrySetResult();
                    }

                    return new ValueTask(Task.FromException(
                        new InvalidOperationException("handler always fails")));
                })
                .WithResilience(s =>
                {
                    s.RetryCount = 0;
                    s.RetryIntervalSeconds = 0;
                    s.TimeoutIntervalSeconds = 30;
                })
                .WithDeadLetterQueue(opts => opts.MaxRetries = 0)
                .Build();
        }, scheduler =>
            scheduler.AddJob<ScheduledWork>("resilient-schedule")
                .Every(FireInterval)
                .DispatchTo<IWorkOrchestrator<ScheduledWork>>(
                    _ => new ScheduledWork("fire"),
                    WorkClass.Batch));

        fx.Time.Advance(FireInterval);
        await fx.Loop.WaitForIdleAsync(TestTimeout);

        using var pollCts = new CancellationTokenSource(TestTimeout);
        while (Volatile.Read(ref fireCount) < 1 && !pollCts.IsCancellationRequested)
        {
            await Task.Delay(20, pollCts.Token);
        }

        await Assert.That(Volatile.Read(ref fireCount)).IsGreaterThanOrEqualTo(1);

        fx.Time.Advance(FireInterval);
        await fx.Loop.WaitForIdleAsync(TestTimeout);

        var reached = await Task.WhenAny(secondFireReady.Task, Task.Delay(TestTimeout));

        await Assert.That(reached == secondFireReady.Task).IsTrue();
        await Assert.That(Volatile.Read(ref fireCount)).IsGreaterThanOrEqualTo(2);
    }

    // =========================================================================
    // Test 4: both event streams emit
    // =========================================================================

    /// <summary>
    /// Verifies that when a scheduled job fires and its work is processed, BOTH
    /// event streams emit:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       The <b>scheduler event stream</b> (the <see cref="SchedulerEventStream"/>
    ///       shared between the registry and the tick loop) publishes a
    ///       <see cref="JobFiredEvent"/>.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       The <b>work event stream</b> (the <see cref="IEventStreamOrchestrator{TWork}"/>
    ///       decorator wired via <c>WithEventStream()</c>) publishes a
    ///       <see cref="WorkCompletedEvent{TWork}"/>.
    ///     </description>
    ///   </item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test subscribes to both streams before it advances the clock, so it cannot miss an
    /// event. <c>SchedulerEventStream</c> channels buffer eagerly at <c>Subscribe</c> time.
    /// <c>EventStreamOrchestrator</c> channels buffer eagerly at <c>GetEventStreamAsync</c> time.
    /// The orchestrator is built with <c>WithEventStream()</c>, so it implements
    /// <c>IEventStreamOrchestrator&lt;TWork&gt;</c>.
    /// </para>
    /// <para>
    /// Each subscription task captures the first event and then stops. If a subscription times out,
    /// its task catches the <see cref="OperationCanceledException"/>, and the test then fails on
    /// the assertion on the matching <c>TaskCompletionSource</c>. At the end, the test cancels both
    /// subscriptions and awaits their tasks.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ScheduledJob_EventStream_PublishesJobFiredAndWorkCompleted()
    {
        var schedulerJobFired = new TaskCompletionSource<JobFiredEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var workCompleted = new TaskCompletionSource<WorkCompletedEvent<ScheduledWork>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var fx = await Fixture.StartAsync(services =>
        {
            services
                .AddWorkOrchestrator<ScheduledWork>(opts => opts.WorkerCount = 1)
                .WithHandler<ScheduledWork>((work, ct) => ValueTask.CompletedTask)
                .WithResilience(s =>
                {
                    s.RetryCount = 0;
                    s.RetryIntervalSeconds = 0;
                    s.TimeoutIntervalSeconds = 30;
                })
                .WithDeadLetterQueue(opts => opts.MaxRetries = 0)
                .WithEventStream()
                .Build();
        }, scheduler =>
            scheduler.AddJob<ScheduledWork>("dual-stream")
                .Every(FireInterval)
                .DispatchTo<IWorkOrchestrator<ScheduledWork>>(
                    _ => new ScheduledWork("ping"),
                    WorkClass.Batch));

        using var schedulerEventCts = new CancellationTokenSource(TestTimeout);
        var schedulerSubscription = fx.SchedulerEvents.Subscribe<JobFiredEvent>(
            schedulerEventCts.Token);
        var schedulerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in schedulerSubscription)
                {
                    schedulerJobFired.TrySetResult(evt);
                    break;
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        var eventOrchestrator = fx.Provider.GetRequiredService<IWorkOrchestrator<ScheduledWork>>()
            as IEventStreamOrchestrator<ScheduledWork>;
        await Assert.That(eventOrchestrator).IsNotNull();

        using var workEventCts = new CancellationTokenSource(TestTimeout);
        var workCompletedStream = eventOrchestrator!.GetEventStreamAsync<WorkCompletedEvent<ScheduledWork>>(
            cancellationToken: workEventCts.Token);
        var workTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in workCompletedStream)
                {
                    workCompleted.TrySetResult(evt);
                    break;
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        fx.Time.Advance(FireInterval);
        await fx.Loop.WaitForIdleAsync(TestTimeout);

        var schedulerEventResult = await Task
            .WhenAny(schedulerJobFired.Task, Task.Delay(TestTimeout));
        await Assert.That(schedulerEventResult == schedulerJobFired.Task).IsTrue();

        var schedulerFired = await schedulerJobFired.Task;
        await Assert.That(schedulerFired.JobName).IsEqualTo("dual-stream");

        var workEventResult = await Task
            .WhenAny(workCompleted.Task, Task.Delay(TestTimeout));
        await Assert.That(workEventResult == workCompleted.Task).IsTrue();

        var workDone = await workCompleted.Task;
        await Assert.That(workDone.Work.Payload).IsEqualTo("ping");
        await Assert.That(workDone.Success).IsTrue();

        await schedulerEventCts.CancelAsync();
        await workEventCts.CancelAsync();
        await Task.WhenAll(schedulerTask, workTask);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// The work item dispatched by the scheduled job. Public so NSubstitute and
    /// the DI system can freely access it.
    /// </summary>
    public sealed record ScheduledWork(string Payload);

    /// <summary>
    /// Shared test fixture: a fully started scheduler + optional orchestrator stack,
    /// with the <see cref="FakeTimeProvider"/> and <see cref="ScheduleTickLoop"/>
    /// exposed for driving fires deterministically.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        // Hold the ServiceProvider so we can dispose it in DisposeAsync (not in StartAsync
        // with `await using`, which would dispose it before the Fixture is returned).
        private readonly ServiceProvider serviceProvider;
        private readonly IReadOnlyList<IHostedService> hostedServices;

        private Fixture(
            FakeTimeProvider time,
            ScheduleTickLoop loop,
            SchedulerEventStream schedulerEvents,
            ServiceProvider serviceProvider,
            IReadOnlyList<IHostedService> hostedServices)
        {
            this.Time = time;
            this.Loop = loop;
            this.SchedulerEvents = schedulerEvents;
            this.serviceProvider = serviceProvider;
            this.hostedServices = hostedServices;
        }

        /// <summary>Gets the fake clock used to advance time.</summary>
        public FakeTimeProvider Time { get; }

        /// <summary>Gets the tick loop for quiescence awaiting.</summary>
        public ScheduleTickLoop Loop { get; }

        /// <summary>Gets the scheduler event stream for subscription.</summary>
        public SchedulerEventStream SchedulerEvents { get; }

        /// <summary>Gets the service provider for resolving test-visible services.</summary>
        public IServiceProvider Provider => this.serviceProvider;

        /// <summary>
        /// Builds and starts a full scheduler + optional orchestrator stack with a
        /// <see cref="FakeTimeProvider"/> pinned to <see cref="Start"/>.
        /// </summary>
        /// <param name="configureOrchestrator">
        /// Callback that registers the orchestrator + decorators (resilience, DLQ,
        /// event stream). Null for tests that do not need a work orchestrator.
        /// </param>
        /// <param name="configureScheduler">
        /// Callback passed to <c>AddScheduler</c> to register DI-time jobs.
        /// </param>
        /// <remarks>
        /// <para>
        /// The method wires the orchestrator and decorator stack first, so that
        /// <c>IWorkOrchestrator&lt;ScheduledWork&gt;</c> is registered before <c>AddScheduler</c>
        /// resolves its <c>DispatchTo&lt;…&gt;</c> factory. Then it wires the scheduler with the
        /// inline job definition.
        /// </para>
        /// <para>
        /// The provider must outlive this method, so the method does not use <c>await using</c>.
        /// <c>DisposeAsync</c> disposes the provider.
        /// </para>
        /// <para>
        /// The method starts all hosted services in registration order: the registration service
        /// first, then the tick loop. This matches the production host startup sequence. Then it
        /// waits for the loop to settle (initial registry load and any startup jobs).
        /// </para>
        /// </remarks>
        /// <returns>A started fixture ready for clock advances.</returns>
        public static async Task<Fixture> StartAsync(
            Action<IServiceCollection>? configureOrchestrator,
            Action<ISchedulerBuilder>? configureScheduler = null)
        {
            var time = new FakeTimeProvider(Start);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(time);

            configureOrchestrator?.Invoke(services);

            services.AddScheduler(configureScheduler);

            var provider = services.BuildServiceProvider();

            var loop = provider.GetServices<IHostedService>().OfType<ScheduleTickLoop>().Single();
            var schedulerEvents = provider.GetRequiredService<SchedulerEventStream>();
            var hostedServices = provider.GetServices<IHostedService>().ToList();

            foreach (var hosted in hostedServices)
            {
                await hosted.StartAsync(CancellationToken.None);
            }

            await loop.WaitForIdleAsync(TimeSpan.FromSeconds(10));

            return new Fixture(time, loop, schedulerEvents, provider, hostedServices);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Stops the hosted services in reverse order before it disposes the provider.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            foreach (var hosted in this.hostedServices.AsEnumerable().Reverse())
            {
                await hosted.StopAsync(CancellationToken.None);
            }

            this.Loop.Dispose();
            await this.serviceProvider.DisposeAsync();
        }
    }
}
