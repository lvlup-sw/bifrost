// =============================================================================
// <copyright file="OpenTelemetryExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
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
    ///   <item><description><c>orchestrator.queue.pending</c> - Gauge for pending queue depth</description></item>
    ///   <item><description><c>orchestrator.workers.active</c> - Gauge for active worker count</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The meter name follows the pattern <c>Bifrost.{TWorkTypeName}</c>.
    /// </para>
    /// <para>
    /// <strong>Important:</strong> The counters and histograms are NOT automatically recorded by the
    /// orchestrator. You must inject <see cref="OrchestratorMetrics{TWork}"/> into your
    /// <see cref="IWorkHandler{TWork}"/> implementation and call the recording methods manually.
    /// The observable gauges (<c>orchestrator.queue.pending</c> and <c>orchestrator.workers.active</c>)
    /// are automatically reported by querying the orchestrator.
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

        // Register metrics with lazy resolution of orchestrator for observable gauges
        builder.Services.TryAddSingleton<OrchestratorMetrics<TWork>>(sp =>
        {
            // Use lazy resolution to avoid circular dependency during build
            return new OrchestratorMetrics<TWork>(
                () => sp.GetService<IWorkOrchestrator<TWork>>());
        });

        return builder;
    }
}