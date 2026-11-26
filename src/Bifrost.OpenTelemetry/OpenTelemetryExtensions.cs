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
    /// Decorator order for attaching the metrics to the base orchestrator (queue-wait hook,
    /// terminal outcomes, and backlog gauges). Registered far below every wrapping decorator
    /// (event stream = 50, autoscaling = 100) so the factory receives the bare
    /// <see cref="WorkOrchestrator{TWork}"/> before any decoration; the factory is a pass-through
    /// and never wraps the orchestrator.
    /// </summary>
    private const int CoreAttachOrder = int.MinValue;

    /// <summary>
    /// Decorator order for the rejected-counter hook attachment: directly above
    /// the rejection-routing decorator
    /// (<see cref="RejectionRoutingRegistration.DecoratorOrder"/>) so its
    /// pass-through factory receives that decorator instance before any further
    /// wrapping (resilience = 25, event stream = 50, autoscaling = 100) applies.
    /// Distinct from <see cref="CoreAttachOrder"/> — the two attach points
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
    ///   <item><description><c>orchestrator.items.processed</c> and <c>orchestrator.items.failed</c> - Counters for items whose last attempt succeeded or failed</description></item>
    ///   <item><description><c>orchestrator.processing.duration</c> - Histogram for processing time across all attempts, tagged by <c>outcome</c></description></item>
    ///   <item><description><c>bifrost.orchestrator.outcomes</c> - Counter for terminal outcomes, tagged by <c>outcome</c> (<c>Succeeded</c>, <c>Failed</c>, <c>Canceled</c>, <c>Abandoned</c>)</description></item>
    ///   <item><description><c>bifrost.orchestrator.handler.attempts</c> - Counter for handler attempts, including dead-letter retries</description></item>
    ///   <item><description><c>bifrost.orchestrator.deadletter.captured</c>, <c>.capture_errors</c>, <c>.evictions</c>, and <c>.notifications_dropped</c> - Dead-letter counters</description></item>
    ///   <item><description><c>bifrost.orchestrator.queue_wait</c> - Histogram for queue wait, tagged by <c>work.class</c></description></item>
    ///   <item><description><c>bifrost.orchestrator.rejected</c> - Counter for admission rejections, tagged by <c>work.class</c> and <c>rejection.reason</c></description></item>
    ///   <item><description><c>orchestrator.queue.pending</c>, <c>bifrost.orchestrator.queue.ready</c>, and <c>bifrost.orchestrator.queue.blocked</c> - Gauges for total, ready, and blocked queue depth</description></item>
    ///   <item><description><c>orchestrator.workers.active</c> - Gauge for active worker count</description></item>
    ///   <item><description><c>orchestrator.items.enqueued</c> and <c>orchestrator.items.deadlettered</c> - Counters that are still recorded manually</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The meter name follows the pattern <c>Bifrost.{TWorkTypeName}</c>.
    /// </para>
    /// <para>
    /// <strong>Automatic recording.</strong> Every instrument except <c>orchestrator.items.enqueued</c>
    /// and <c>orchestrator.items.deadlettered</c> is recorded by the orchestrator. Processing counts and
    /// duration come from the orchestrator's terminal outcome stage: one record per accepted item,
    /// after the handler pipeline and its scope exit and the key slot is released, and before the
    /// worker reports idle. They are therefore the same for every call order of
    /// <c>WithOpenTelemetry</c>, <c>WithDeadLetterQueue</c>, <c>WithEventStream</c>, and
    /// <c>WithHandlerDecorator</c>. Rejections are counted at admission by the rejection-routing
    /// decorator, independent of whether a dead-letter queue is configured.
    /// </para>
    /// <para>
    /// <strong>Moving from manual metrics.</strong> Before 0.7.0, handlers called
    /// <c>RecordProcessed</c> and <c>RecordFailed</c> themselves. Those methods are now obsolete:
    /// remove the calls, or each item is counted twice. A manual call in the handler also counted
    /// each dead-letter retry as a failure; the automatic counters count each item once, after its
    /// last attempt, and count attempts separately in <c>bifrost.orchestrator.handler.attempts</c>.
    /// See <c>docs/migrations/2026-11-automatic-metrics.md</c>.
    /// </para>
    /// <para>
    /// <strong>No work keys in labels.</strong> Every tag value comes from a fixed set of enum member
    /// names. Work items and work keys never appear as tags, and the ready and blocked gauges are
    /// aggregate counts across all keys, so keyed dispatch with many keys adds no series.
    /// </para>
    /// <para>
    /// <strong>Registration details.</strong> The metrics factory resolves the orchestrator lazily
    /// for the observable gauges, which avoids a circular dependency during the build.
    /// </para>
    /// <para>
    /// A pass-through decorator at the innermost order (<c>CoreAttachOrder</c>) attaches the metrics to
    /// the bare <c>WorkOrchestrator&lt;TWork&gt;</c> before any wrapping decorator applies: the
    /// queue-wait hook, the terminal outcome observer, the outcome counters, and the backlog snapshot.
    /// These members are internal on the concrete type (reached through <c>InternalsVisibleTo</c>), so
    /// the factory uses a pattern match instead of a wider public <see cref="IWorkOrchestrator{TWork}"/>
    /// surface. The metrics instance is resolved once at build time and bound as closure-free
    /// method-group delegates, which keeps the per-dequeue and per-item record paths allocation-free.
    /// A guard stops repeated calls from registering a duplicate decorator order, because
    /// <c>Build()</c> rejects duplicates.
    /// </para>
    /// <para>
    /// Rejections are always counted (DR-6), independent of the dead-letter configuration. The method
    /// makes sure that the rejection-routing decorator exists (idempotent and shared with
    /// <c>WithDeadLetterQueue</c>, in any call order). It then attaches the rejected counter to that
    /// decorator through a pass-through registered directly above it, in the same pattern as the
    /// core attach. The hook is bound once at build time as a method-group delegate. The
    /// per-rejection record path is allocation-free, because the <c>work.class</c> and
    /// <c>rejection.reason</c> tag values are cached.
    /// </para>
    /// </remarks>
    /// <example>
    /// <para>Register the orchestrator with OpenTelemetry. No handler code is needed for metrics:</para>
    /// <code>
    /// services.AddWorkOrchestrator&lt;MyWork&gt;()
    ///     .WithHandler&lt;MyWork, MyWorkHandler&gt;()
    ///     .WithOpenTelemetry()
    ///     .Build();
    ///
    /// services.AddOpenTelemetry()
    ///     .WithMetrics(metrics =&gt; metrics.AddMeter("Bifrost.MyWork"));
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

        if (!builder.Decorators.Any(d => d.Order == CoreAttachOrder))
        {
            builder.Decorators.Add(new DecoratorRegistration<TWork>(
                CoreAttachOrder,
                static (sp, orchestrator) =>
                {
                    if (orchestrator is WorkOrchestrator<TWork> concrete)
                    {
                        sp.GetRequiredService<OrchestratorMetrics<TWork>>().Attach(concrete);
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