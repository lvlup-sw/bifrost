// =============================================================================
// <copyright file="WorkOrchestratorOptions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.ComponentModel.DataAnnotations;

namespace Bifrost.Core;

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

    /// <summary>
    /// Gets or sets the dispatch strategy selecting the orchestrator's internal
    /// queue binding.
    /// </summary>
    /// <value>
    /// The strategy. Default is <see cref="DispatchStrategy.Fifo"/>, preserving the
    /// orchestrator's pre-existing strict-FIFO, producer-wait semantics.
    /// </value>
    /// <remarks>
    /// Selection is enum/factory-based — the orchestrator constructs the binding
    /// directly from this value with no reflective resolution (trim/AOT-safe). The
    /// priority strategies change the asynchronous enqueue semantics to fail-fast
    /// admission; see <see cref="DispatchStrategy"/> for the semantics table.
    /// </remarks>
    public DispatchStrategy DispatchStrategy { get; set; } = DispatchStrategy.Fifo;

    /// <summary>
    /// Gets or sets the class-based priority dispatch options consumed by the
    /// priority strategies.
    /// </summary>
    /// <value>
    /// The priority options. Eagerly defaulted to a new
    /// <see cref="PriorityDispatchOptions"/> so configure delegates can mutate it
    /// without null checks.
    /// </value>
    /// <remarks>
    /// Ignored when <see cref="DispatchStrategy"/> is
    /// <see cref="DispatchStrategy.Fifo"/>. Watermark monotonicity is validated by
    /// the selected binding's constructor at orchestrator construction.
    /// </remarks>
    public PriorityDispatchOptions Priority { get; set; } = new();
}