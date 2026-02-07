// =============================================================================
// <copyright file="AutoscalingExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;

using Bifrost.Autoscaling;
using Bifrost.Decorators;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bifrost.DependencyInjection;

/// <summary>
/// Extension methods for adding autoscaling support to the work orchestrator.
/// </summary>
public static class AutoscalingExtensions
{
    /// <summary>
    /// The decorator order for autoscaling (outer decorator).
    /// </summary>
    private const int AutoscalingDecoratorOrder = 100;

    /// <summary>
    /// Adds autoscaling support to the work orchestrator.
    /// </summary>
    /// <typeparam name="TWork">The type of work item to process.</typeparam>
    /// <param name="builder">The work orchestrator builder.</param>
    /// <param name="configure">Optional configuration action for autoscaling options.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <remarks>
    /// <para>
    /// This extension registers the following services:
    /// <list type="bullet">
    ///   <item><description><see cref="IWorkerMetrics"/> - Thread-safe metrics tracking</description></item>
    ///   <item><description><see cref="AutoscalingEngine"/> - Scaling decision engine</description></item>
    ///   <item><description><see cref="AutoscalingOrchestrator{TWork}"/> - Decorator for metrics collection</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The autoscaling decorator is added as an outer decorator, ensuring metrics
    /// are recorded before work enters the inner orchestrator.
    /// </para>
    /// </remarks>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "AutoscalingOptions type is fully preserved and known at compile time")]
    public static WorkOrchestratorBuilder<TWork> WithAutoscaling<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Action<AutoscalingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Register autoscaling options with DataAnnotations validation
        builder.Services.AddOptions<AutoscalingOptions>()
            .Configure(configure ?? (_ => { }))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Register autoscaling services (TryAdd to avoid duplicates)
        builder.Services.TryAddSingleton<IWorkerMetrics, WorkerMetrics>();
        builder.Services.TryAddSingleton<AutoscalingEngine>();

        // Add the autoscaling decorator
        builder.Decorators.Add(new DecoratorRegistration<TWork>(
            AutoscalingDecoratorOrder,
            (sp, inner) => new AutoscalingOrchestrator<TWork>(
                inner,
                sp.GetRequiredService<IWorkerMetrics>())));

        return builder;
    }
}