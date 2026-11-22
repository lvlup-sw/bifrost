// =============================================================================
// <copyright file="OpenTelemetryExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Decorators;
using Bifrost.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bifrost.OpenTelemetry;

/// <summary>
/// Extension methods for adding OpenTelemetry instrumentation to <see cref="IWorkOrchestrator{TWork}"/>.
/// </summary>
public static class OpenTelemetryExtensions
{
    /// <summary>
    /// Decorator order for the queue-wait hook attachment. Registered far below every
    /// wrapping decorator (event stream = 50, autoscaling = 100) so the factory
    /// receives the bare <see cref="WorkOrchestrator{TWork}"/> before any decoration;
    /// the factory is a pass-through and never wraps the orchestrator.
    /// </summary>
    private const int QueueWaitAttachOrder = int.MinValue;

    /// <summary>
    /// Decorator order for the rejected-counter hook attachment: directly above
    /// the rejection-routing decorator
    /// (<see cref="RejectionRoutingRegistration.DecoratorOrder"/>) so its
    /// pass-through factory receives that decorator instance before any further
    /// wrapping (resilience = 25, event stream = 50, autoscaling = 100) applies.
    /// Distinct from <see cref="QueueWaitAttachOrder"/> — the two attach points
    /// never collide.
    /// </summary>
    private const int RejectedCounterAttachOrder = RejectionRoutingRegistration.DecoratorOrder + 1;

    /// <summary>
    /// Adds OpenTelemetry metrics instrumentation to the work orchestrator.
    /// </summary>
    /// <typeparam name="TWork">The type of work items.</typeparam>
    /// <param name="builder">The orchestrator builder.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="builder"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This extension registers <see cref="OrchestratorMetrics{TWork}"/> which provides:
    /// <list type="bullet">
    ///   <item><description><c>orchestrator.items.enqueued</c> - Counter for enqueued items</description></item>
    ///   <item><description><c>orchestrator.items.processed</c> - Counter for processed items</description></item>
    ///   <item><description><c>orchestrator.items.failed</c> - Counter for failed items</description></item>
    ///   <item><description><c>orchestrator.processing.duration</c> - Histogram for processing time</description></item>
    ///   <item><description><c>bifrost.orchestrator.queue_wait</c> - Histogram for queue wait, tagged by <c>work.class</c></description></item>
    ///   <item><description><c>bifrost.orchestrator.rejected</c> - Counter for admission rejections, tagged by <c>work.class</c> and <c>rejection.reason</c></description></item>
    ///   <item><description><c>orchestrator.queue.pending</c> - Gauge for pending queue depth</description></item>
    ///   <item><description><c>orchestrator.workers.active</c> - Gauge for active worker count</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The meter name follows the pattern <c>Bifrost.{TWorkTypeName}</c>.
    /// </para>
    /// <para>
    /// <strong>Important:</strong> The counters and the processing-duration histogram are NOT
    /// automatically recorded by the orchestrator. You must inject
    /// <see cref="OrchestratorMetrics{TWork}"/> into your <see cref="IWorkHandler{TWork}"/>
    /// implementation and call the recording methods manually.
    /// The observable gauges (<c>orchestrator.queue.pending</c> and <c>orchestrator.workers.active</c>)
    /// are automatically reported by querying the orchestrator, the
    /// <c>bifrost.orchestrator.queue_wait</c> histogram is recorded automatically at dequeue via
    /// the orchestrator's internal queue-wait hook (see
    /// <see cref="OrchestratorMetrics{TWork}.QueueWait"/> for the Stage-2 evidence recipe), and
    /// the <c>bifrost.orchestrator.rejected</c> counter is recorded automatically on admission
    /// rejection via the rejection-routing decorator (see
    /// <see cref="OrchestratorMetrics{TWork}.Rejected"/>) — independent of whether a dead-letter
    /// queue is configured.
    /// </para>
    /// <para>
    /// <strong>Registration details.</strong> The metrics factory resolves the orchestrator lazily
    /// for the observable gauges, which avoids a circular dependency during the build.
    /// </para>
    /// <para>
    /// A pass-through decorator at the innermost order (<c>QueueWaitAttachOrder</c>) attaches the
    /// queue-wait hook. Its factory receives the bare <c>WorkOrchestrator&lt;TWork&gt;</c> before any
    /// wrapping decorator applies. The hook property is internal on the concrete type (reached through
    /// <c>InternalsVisibleTo</c>), so the factory uses a pattern match instead of a wider public
    /// <see cref="IWorkOrchestrator{TWork}"/> surface. The metrics instance is resolved once at build
    /// time and bound as a closure-free method-group delegate, which keeps the per-dequeue record path
    /// allocation-free. A guard stops repeated calls from registering a duplicate decorator order,
    /// because <c>Build()</c> rejects duplicates.
    /// </para>
    /// <para>
    /// Rejections are always counted (DR-6), independent of the dead-letter configuration. The method
    /// makes sure that the rejection-routing decorator exists (idempotent and shared with
    /// <c>WithDeadLetterQueue</c>, in any call order). It then attaches the rejected counter to that
    /// decorator through a pass-through registered directly above it, in the same pattern as the
    /// queue-wait attach. The hook is bound once at build time as a method-group delegate. The
    /// per-rejection record path is allocation-free, because the <c>work.class</c> and
    /// <c>rejection.reason</c> tag values are cached.
    /// </para>
    /// </remarks>
    /// <example>
    /// <para>Register the orchestrator with OpenTelemetry:</para>
    /// <code>
    /// services.AddWorkOrchestrator&lt;MyWork&gt;()
    ///     .WithHandler&lt;MyWorkHandler&gt;()
    ///     .WithOpenTelemetry()
    ///     .Build();
    /// </code>
    /// <para>Wire metrics in your handler implementation:</para>
    /// <code>
    /// public class MyWorkHandler : IWorkHandler&lt;MyWork&gt;
    /// {
    ///     private readonly OrchestratorMetrics&lt;MyWork&gt; _metrics;
    ///
    ///     public MyWorkHandler(OrchestratorMetrics&lt;MyWork&gt; metrics)
    ///     {
    ///         _metrics = metrics;
    ///     }
    ///
    ///     public async ValueTask HandleAsync(MyWork work, CancellationToken ct)
    ///     {
    ///         var sw = Stopwatch.StartNew();
    ///         try
    ///         {
    ///             // Process work item
    ///             await ProcessAsync(work, ct);
    ///             _metrics.RecordProcessed(sw.ElapsedMilliseconds);
    ///         }
    ///         catch
    ///         {
    ///             _metrics.RecordFailed(sw.ElapsedMilliseconds);
    ///             throw;
    ///         }
    ///     }
    /// }
    /// </code>
    /// </example>
    public static WorkOrchestratorBuilder<TWork> WithOpenTelemetry<TWork>(
        this WorkOrchestratorBuilder<TWork> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<OrchestratorMetrics<TWork>>(sp =>
        {
            return new OrchestratorMetrics<TWork>(
                () => sp.GetService<IWorkOrchestrator<TWork>>());
        });

        if (!builder.Decorators.Any(d => d.Order == QueueWaitAttachOrder))
        {
            builder.Decorators.Add(new DecoratorRegistration<TWork>(
                QueueWaitAttachOrder,
                static (sp, orchestrator) =>
                {
                    if (orchestrator is WorkOrchestrator<TWork> concrete)
                    {
                        concrete.QueueWaitObserved =
                            sp.GetRequiredService<OrchestratorMetrics<TWork>>().RecordQueueWait;
                    }

                    return orchestrator;
                }));
        }

        RejectionRoutingRegistration.EnsureRegistered(builder);

        if (!builder.Decorators.Any(d => d.Order == RejectedCounterAttachOrder))
        {
            builder.Decorators.Add(new DecoratorRegistration<TWork>(
                RejectedCounterAttachOrder,
                static (sp, orchestrator) =>
                {
                    if (orchestrator is RejectionRoutingOrchestrator<TWork> routing)
                    {
                        routing.RejectionObserved =
                            sp.GetRequiredService<OrchestratorMetrics<TWork>>().RecordRejected;
                    }

                    return orchestrator;
                }));
        }

        return builder;
    }
}