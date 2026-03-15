// =============================================================================
// <copyright file="AutoscalingExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;

using Bifrost.Autoscaling;
using Bifrost.Core;
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
    /// <param name="options">Optional pre-configured autoscaling options. When null, defaults are used.</param>
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
    /// <para>
    /// <see cref="AutoscalingOptions"/> uses init-only properties, so options must be
    /// provided as a pre-configured instance via object initializer syntax rather than
    /// a mutation delegate.
    /// </para>
    /// </remarks>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "AutoscalingOptions type is fully preserved and known at compile time")]
    public static WorkOrchestratorBuilder<TWork> WithAutoscaling<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        AutoscalingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Register the immutable options instance (init-only properties
        // prevent mutation after construction, so we accept a pre-built instance
        // rather than an Action<T> mutation delegate)
        builder.Services.AddSingleton<IOptions<AutoscalingOptions>>(
            Options.Create(options ?? new AutoscalingOptions()));

        // Register autoscaling services (TryAdd to avoid duplicates)
        builder.Services.TryAddSingleton<IWorkerMetrics, WorkerMetrics>();
        builder.Services.TryAddSingleton<AutoscalingEngine>();

        // Register worker registry
        builder.Services.TryAddSingleton<IWorkerRegistry, WorkerRegistry>();

        // Register the autoscaling coordinator (bridges orchestrator → engine)
        builder.Services.TryAddSingleton<IAutoscalingCoordinator>(sp =>
        {
            var orchestrator = sp.GetRequiredService<IWorkOrchestrator<TWork>>();
            var registry = sp.GetRequiredService<IWorkerRegistry>();
            return new AutoscalingCoordinator<TWork>(orchestrator, registry);
        });

        // Add the autoscaling decorator
        builder.Decorators.Add(new DecoratorRegistration<TWork>(
            AutoscalingDecoratorOrder,
            (sp, inner) => new AutoscalingOrchestrator<TWork>(
                inner,
                sp.GetRequiredService<IWorkerRegistry>(),
                sp.GetRequiredService<IWorkerMetrics>(),
                sp.GetRequiredService<IOptions<AutoscalingOptions>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AutoscalingOrchestrator<TWork>>>())));

        return builder;
    }
}