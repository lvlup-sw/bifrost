// =============================================================================
// <copyright file="WorkOrchestratorOptions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.ComponentModel.DataAnnotations;

namespace Levelup.Channels.Core;

/// <summary>
/// Configuration options for the work orchestrator.
/// </summary>
/// <remarks>
/// These options control the channel capacity and worker count
/// for the work orchestrator.
/// </remarks>
public class WorkOrchestratorOptions
{
    /// <summary>
    /// Gets or sets the maximum capacity of the internal channel.
    /// </summary>
    /// <value>The channel capacity. Default is 128.</value>
    /// <remarks>
    /// Higher capacity allows more buffering but uses more memory.
    /// Lower capacity provides faster backpressure feedback.
    /// </remarks>
    [Range(1, 10000)]
    public int Capacity { get; set; } = 128;

    /// <summary>
    /// Gets or sets the number of concurrent workers processing items.
    /// </summary>
    /// <value>The worker count. Default is 2.</value>
    /// <remarks>
    /// Increase workers for CPU-bound work on multi-core systems.
    /// Keep lower for I/O-bound work to avoid connection exhaustion.
    /// </remarks>
    [Range(1, 100)]
    public int WorkerCount { get; set; } = 2;
}
