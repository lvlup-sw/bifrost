// =============================================================================
// <copyright file="PriorityDispatchExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.DependencyInjection;

/// <summary>Configures explicit priority ordering.</summary>
public static class PriorityDispatchExtensions
{
    /// <summary>Selects exact or relaxed priority ordering with immediate rejection at capacity.</summary>
    /// <typeparam name="TWork">The work type.</typeparam>
    /// <param name="builder">The builder.</param>
    /// <param name="ordering">PriorityExact or PriorityRelaxed.</param>
    /// <param name="configure">Optional priority policy configuration.</param>
    /// <returns>The builder.</returns>
    public static WorkOrchestratorBuilder<TWork> UsePriorityDispatch<TWork>(
        this WorkOrchestratorBuilder<TWork> builder, DispatchStrategy ordering,
        Action<PriorityDispatchOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (ordering is not (DispatchStrategy.PriorityExact or DispatchStrategy.PriorityRelaxed))
            throw new ArgumentOutOfRangeException(nameof(ordering));
        builder.Services.Configure<WorkOrchestratorOptions>(WorkOptions.Name<TWork>(), options =>
        {
            options.DispatchStrategy = ordering;
            options.FullMode = QueueFullMode.Reject;
            configure?.Invoke(options.Priority);
        });
        return builder;
    }
}
