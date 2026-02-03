// =============================================================================
// <copyright file="IAutoscalingEngine.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Levelup.Channels.Autoscaling;

/// <summary>
/// Defines the contract for the autoscaling engine.
/// </summary>
/// <remarks>
/// <para>
/// The autoscaling engine periodically evaluates utilization metrics and makes
/// scaling decisions based on configured watermarks and thresholds.
/// </para>
/// <para>
/// The engine supports start/stop lifecycle management, making it suitable
/// for use as a hosted service in ASP.NET Core applications.
/// </para>
/// </remarks>
public interface IAutoscalingEngine
{
    /// <summary>
    /// Gets a value indicating whether the engine is currently running.
    /// </summary>
    /// <value><c>true</c> if the engine is running; otherwise, <c>false</c>.</value>
    bool IsRunning { get; }

    /// <summary>
    /// Gets the effective minimum number of workers.
    /// </summary>
    /// <value>
    /// The larger of the configured minimum workers and the initial worker count
    /// when the engine was created.
    /// </value>
    /// <remarks>
    /// This prevents scaling below the initial worker count, even if the
    /// configured minimum is lower.
    /// </remarks>
    int EffectiveMinWorkers { get; }

    /// <summary>
    /// Gets the autoscaling configuration.
    /// </summary>
    /// <value>The autoscaling options.</value>
    AutoscalingOptions Configuration { get; }

    /// <summary>
    /// Starts the autoscaling engine.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the engine has started.</returns>
    /// <remarks>
    /// This method is idempotent. Calling it when already running has no effect.
    /// </remarks>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the autoscaling engine.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the engine has stopped.</returns>
    /// <remarks>
    /// This method is idempotent. Calling it when not running has no effect.
    /// </remarks>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates the current state and returns a scaling decision.
    /// </summary>
    /// <returns>A scaling decision indicating what action to take.</returns>
    /// <remarks>
    /// This method is called periodically by the internal timer when the engine
    /// is running. It can also be called manually for testing or immediate evaluation.
    /// </remarks>
    ScalingDecision EvaluateScaling();

    /// <summary>
    /// Occurs when a scaling decision is made.
    /// </summary>
    /// <remarks>
    /// This event is raised for every evaluation, regardless of whether
    /// an actual scaling action is required.
    /// </remarks>
    event EventHandler<ScalingDecisionEventArgs>? ScalingDecisionMade;
}
