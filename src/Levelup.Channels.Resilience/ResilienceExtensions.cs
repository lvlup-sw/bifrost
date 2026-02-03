// =============================================================================
// <copyright file="ResilienceExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Levelup.Channels.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Levelup.Channels.Resilience;

/// <summary>
/// Extension methods for adding resilience support to the work orchestrator.
/// </summary>
public static class ResilienceExtensions
{
    /// <summary>
    /// The decorator order for resilience (inner to autoscaling).
    /// </summary>
    private const int ResilienceDecoratorOrder = 50;

    /// <summary>
    /// Adds resilience support to the work orchestrator using Polly policies.
    /// </summary>
    /// <typeparam name="TWork">The type of work item to process.</typeparam>
    /// <param name="builder">The work orchestrator builder.</param>
    /// <param name="configure">Optional configuration action for resiliency settings.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when builder is null.</exception>
    /// <remarks>
    /// <para>
    /// This extension registers the following services:
    /// <list type="bullet">
    ///   <item><description><see cref="ResiliencySettings"/> - Configuration via Options pattern</description></item>
    ///   <item><description><see cref="ResilientOrchestrator{TWork}"/> - Decorator with Polly policies</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The resilience decorator is added at order 50, making it inner to the autoscaling
    /// decorator (order 100). This ensures resilience policies are applied to individual
    /// enqueue operations before metrics are recorded.
    /// </para>
    /// <para>
    /// The decorator applies the following Polly policies:
    /// <list type="bullet">
    ///   <item><description>Retry with exponential backoff for transient failures</description></item>
    ///   <item><description>Timeout to prevent indefinite hangs</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public static WorkOrchestratorBuilder<TWork> WithResilience<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Action<ResiliencySettings>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Register resiliency options
        builder.Services.AddOptions<ResiliencySettings>()
            .Configure(configure ?? (_ => { }));

        // Add the resilience decorator
        builder.Decorators.Add(new DecoratorRegistration<TWork>(
            ResilienceDecoratorOrder,
            (sp, inner) => new ResilientOrchestrator<TWork>(
                inner,
                sp.GetRequiredService<IOptions<ResiliencySettings>>(),
                sp.GetRequiredService<ILogger<ResilientOrchestrator<TWork>>>())));

        return builder;
    }
}
