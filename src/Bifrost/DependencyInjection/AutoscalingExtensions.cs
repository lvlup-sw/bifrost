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

        var key = typeof(TWork);
        builder.Services.AddKeyedSingleton<IOptions<AutoscalingOptions>>(key,
            Options.Create(options ?? new AutoscalingOptions()));
        builder.Services.AddKeyedSingleton<IWorkerMetrics, WorkerMetrics>(key);
        builder.Services.AddKeyedSingleton<IWorkerRegistry, WorkerRegistry>(key);
        builder.Services.AddKeyedSingleton<IAutoscalingCoordinator>(key, (sp, _) =>
            new AutoscalingCoordinator<TWork>(sp.GetRequiredService<IWorkOrchestrator<TWork>>(),
                sp.GetRequiredKeyedService<IWorkerRegistry>(key)));
        builder.Services.AddKeyedSingleton<AutoscalingEngine>(key, (sp, _) =>
            new AutoscalingEngine(sp.GetRequiredKeyedService<IOptions<AutoscalingOptions>>(key),
                sp.GetRequiredKeyedService<IAutoscalingCoordinator>(key),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AutoscalingEngine>>()));
        builder.Services.AddKeyedSingleton<IAutoscalingEngine>(key, (sp, _) =>
            sp.GetRequiredKeyedService<AutoscalingEngine>(key));

        // Add the autoscaling decorator
        builder.Decorators.Add(new DecoratorRegistration<TWork>(
            AutoscalingDecoratorOrder,
            (sp, inner) => new AutoscalingOrchestrator<TWork>(
                inner,
                sp.GetRequiredKeyedService<IWorkerRegistry>(key),
                sp.GetRequiredKeyedService<IWorkerMetrics>(key),
                sp.GetRequiredKeyedService<IOptions<AutoscalingOptions>>(key),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AutoscalingOrchestrator<TWork>>>())));

        return builder;
    }
}
