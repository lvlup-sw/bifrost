// =============================================================================
// <copyright file="HealthCheckExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;
using Bifrost.Core;
using Bifrost.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Bifrost.HealthChecks;

/// <summary>
/// Extension methods for adding health checks to the work orchestrator.
/// </summary>
public static class HealthCheckExtensions
{
    /// <summary>
    /// The default health check name prefix.
    /// </summary>
    private const string DefaultHealthCheckNamePrefix = "WorkOrchestrator";

    /// <summary>
    /// Adds a health check for the work orchestrator.
    /// </summary>
    /// <typeparam name="TWork">The type of work item to process.</typeparam>
    /// <param name="builder">The builder to extend.</param>
    /// <param name="name">Optional custom name for the health check.</param>
    /// <returns>The builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers a <see cref="WorkOrchestratorHealthCheck{TWork}"/> that monitors queue utilization
    /// and reports <see cref="HealthStatus.Degraded"/> when the queue is above 95% capacity.
    /// </para>
    /// <para>
    /// If no name is provided, the health check name will be "WorkOrchestrator&lt;TWork&gt;".
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddWorkOrchestrator&lt;MyWork&gt;()
    ///     .WithHandler&lt;MyWorkHandler&gt;()
    ///     .WithHealthChecks()
    ///     .Build();
    /// </code>
    /// </example>
    public static WorkOrchestratorBuilder<TWork> WithHealthChecks<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var healthCheckName = name ?? $"{DefaultHealthCheckNamePrefix}<{typeof(TWork).Name}>";

        // Register the health check directly on the service collection
        builder.Services.AddHealthChecks()
            .Add(new HealthCheckRegistration(
                healthCheckName,
                sp => new WorkOrchestratorHealthCheck<TWork>(sp.GetRequiredService<IWorkOrchestrator<TWork>>()),
                failureStatus: null,
                tags: null));

        return builder;
    }

    /// <summary>
    /// Adds health check services and registers a health check for the work orchestrator.
    /// </summary>
    /// <typeparam name="TWork">The type of work item to process.</typeparam>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="name">Optional custom name for the health check.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// This method registers health check services if not already registered,
    /// then adds the orchestrator health check. Call this after building the orchestrator.
    /// </remarks>
    public static IServiceCollection AddWorkOrchestratorHealthCheck<TWork>(
        this IServiceCollection services,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var healthCheckName = name ?? $"{DefaultHealthCheckNamePrefix}<{typeof(TWork).Name}>";

        services.AddHealthChecks()
            .Add(new HealthCheckRegistration(
                healthCheckName,
                sp => new WorkOrchestratorHealthCheck<TWork>(sp.GetRequiredService<IWorkOrchestrator<TWork>>()),
                failureStatus: null,
                tags: null));

        return services;
    }

    /// <summary>
    /// Adds health checks for autoscaling components.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <returns>The builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers health checks for:
    /// <list type="bullet">
    ///   <item><description><see cref="AutoscalingHealthCheck"/> - Monitors autoscaling capacity</description></item>
    ///   <item><description><see cref="WorkerRegistryHealthCheck"/> - Monitors worker registry state</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Both health checks are tagged with "autoscaling" for filtering.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddHealthChecks()
    ///     .AddAutoscalingHealthChecks();
    /// </code>
    /// </example>
    public static IHealthChecksBuilder AddAutoscalingHealthChecks(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .Add(new HealthCheckRegistration(
                "autoscaling-engine",
                sp => new AutoscalingHealthCheck(
                    sp.GetRequiredService<IWorkerRegistry>(),
                    sp.GetRequiredService<IOptions<AutoscalingOptions>>()),
                failureStatus: HealthStatus.Unhealthy,
                tags: ["autoscaling"]))
            .Add(new HealthCheckRegistration(
                "worker-registry",
                sp => new WorkerRegistryHealthCheck(
                    sp.GetRequiredService<IWorkerRegistry>()),
                failureStatus: HealthStatus.Unhealthy,
                tags: ["autoscaling"]));

        return builder;
    }
}