// =============================================================================
// <copyright file="AutoscalingOptions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.ComponentModel.DataAnnotations;

namespace Bifrost.Autoscaling;

/// <summary>
/// Configuration options for the autoscaling engine.
/// </summary>
/// <remarks>
/// <para>
/// These options control the scaling behavior of the work orchestrator,
/// including worker limits, watermarks for scaling decisions, and cooldown periods.
/// </para>
/// <para>
/// The watermark-based approach ensures that scaling happens when utilization
/// crosses thresholds, preventing oscillation through the cooldown period.
/// </para>
/// </remarks>
public class AutoscalingOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether autoscaling is enabled.
    /// </summary>
    /// <value>True if autoscaling is enabled; false otherwise. Default is true.</value>
    /// <remarks>
    /// When disabled, the autoscaling decorator passes through to the inner
    /// orchestrator without tracking metrics or managing dynamic workers.
    /// This reduces overhead when autoscaling is not needed.
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Gets or sets the minimum number of workers to maintain.
    /// </summary>
    /// <value>The minimum worker count. Default is 1.</value>
    /// <remarks>
    /// The autoscaler will never scale below this number of workers,
    /// ensuring at least one worker is always available to process work.
    /// </remarks>
    [Range(1, 100)]
    public int MinWorkers { get; init; } = 1;

    /// <summary>
    /// Gets or sets the maximum number of workers allowed.
    /// </summary>
    /// <value>The maximum worker count. Default is 16.</value>
    /// <remarks>
    /// The autoscaler will never scale above this number of workers,
    /// providing an upper bound on resource consumption.
    /// </remarks>
    [Range(1, 100)]
    public int MaxWorkers { get; init; } = 16;

    /// <summary>
    /// Gets or sets the high watermark threshold for scaling up.
    /// </summary>
    /// <value>The high watermark ratio. Default is 0.8 (80%).</value>
    /// <remarks>
    /// When utilization exceeds this threshold, the autoscaler will
    /// add workers to handle the increased load.
    /// </remarks>
    [Range(0.0, 1.0)]
    public double HighWatermark { get; init; } = 0.8;

    /// <summary>
    /// Gets or sets the low watermark threshold for scaling down.
    /// </summary>
    /// <value>The low watermark ratio. Default is 0.3 (30%).</value>
    /// <remarks>
    /// When utilization drops below this threshold, the autoscaler will
    /// remove workers to reduce resource consumption.
    /// </remarks>
    [Range(0.0, 1.0)]
    public double LowWatermark { get; init; } = 0.3;

    /// <summary>
    /// Gets or sets the cooldown period between scaling decisions.
    /// </summary>
    /// <value>The cooldown duration. Default is 30 seconds.</value>
    /// <remarks>
    /// After a scaling action, the autoscaler will wait for this duration
    /// before making another decision, preventing oscillation.
    /// </remarks>
    public TimeSpan CooldownPeriod { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the number of workers to add during scale up.
    /// </summary>
    /// <value>The scale up step. Default is 2.</value>
    /// <remarks>
    /// A larger step provides faster scaling but may overshoot optimal capacity.
    /// A smaller step provides more gradual scaling at the cost of responsiveness.
    /// </remarks>
    [Range(1, 10)]
    public int ScaleUpStep { get; init; } = 2;

    /// <summary>
    /// Gets or sets the number of workers to remove during scale down.
    /// </summary>
    /// <value>The scale down step. Default is 1.</value>
    /// <remarks>
    /// Scale down is typically more conservative than scale up to avoid
    /// removing workers that may be needed again shortly.
    /// </remarks>
    [Range(1, 10)]
    public int ScaleDownStep { get; init; } = 1;

    /// <summary>
    /// Gets or sets the interval between scaling evaluations.
    /// </summary>
    /// <value>The check interval. Default is 5 seconds.</value>
    /// <remarks>
    /// The autoscaling engine evaluates utilization at this interval
    /// and makes scaling decisions based on the configured watermarks.
    /// A shorter interval provides more responsive scaling but increases CPU usage.
    /// </remarks>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(5);
}