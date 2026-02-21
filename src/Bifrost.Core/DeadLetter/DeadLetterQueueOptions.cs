// =============================================================================
// <copyright file="DeadLetterQueueOptions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.ComponentModel.DataAnnotations;

namespace Bifrost.Core.DeadLetter;

/// <summary>
/// Configuration options for the dead letter queue.
/// </summary>
/// <remarks>
/// These options control the capacity of the dead letter queue
/// and the maximum number of retries before dead-lettering.
/// </remarks>
public sealed class DeadLetterQueueOptions
{
    /// <summary>
    /// Gets or sets the maximum capacity of the dead letter queue.
    /// </summary>
    /// <value>The queue capacity. Default is 1000.</value>
    /// <remarks>
    /// When the queue is full, the oldest item is dropped to make room.
    /// </remarks>
    [Range(1, int.MaxValue)]
    public int Capacity { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the maximum number of retry attempts before dead-lettering.
    /// </summary>
    /// <value>The maximum retry count. Default is 3.</value>
    /// <remarks>
    /// A value of 0 means no retries; work is dead-lettered on the first failure.
    /// </remarks>
    [Range(0, 100)]
    public int MaxRetries { get; set; } = 3;
}
