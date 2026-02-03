// =============================================================================
// <copyright file="AutoscalingEngineHostedService.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Microsoft.Extensions.Hosting;

namespace Bifrost.Autoscaling;

/// <summary>
/// A hosted service that manages the lifecycle of the autoscaling engine.
/// </summary>
/// <remarks>
/// <para>
/// This hosted service starts the autoscaling engine when the application starts
/// and stops it when the application is shutting down, ensuring proper cleanup.
/// </para>
/// <para>
/// Register this service in the dependency injection container to enable
/// automatic autoscaling for the application:
/// <code>
/// services.AddHostedService&lt;AutoscalingEngineHostedService&gt;();
/// </code>
/// </para>
/// </remarks>
public sealed class AutoscalingEngineHostedService : IHostedService
{
    private readonly IAutoscalingEngine _engine;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoscalingEngineHostedService"/> class.
    /// </summary>
    /// <param name="engine">The autoscaling engine to manage.</param>
    /// <exception cref="ArgumentNullException">Thrown when engine is null.</exception>
    public AutoscalingEngineHostedService(IAutoscalingEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    /// <summary>
    /// Starts the autoscaling engine.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the engine has started.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
        => _engine.StartAsync(cancellationToken);

    /// <summary>
    /// Stops the autoscaling engine.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the engine has stopped.</returns>
    public Task StopAsync(CancellationToken cancellationToken)
        => _engine.StopAsync(cancellationToken);
}
