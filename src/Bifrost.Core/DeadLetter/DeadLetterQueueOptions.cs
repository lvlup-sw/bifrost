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
    /// <para>
    /// This is the number of <b>retries after the initial attempt</b>. Total processing
    /// attempts = 1 (initial) + MaxRetries.
    /// </para>
    /// <para>
    /// Examples:
    /// <list type="bullet">
    ///   <item><description><c>MaxRetries = 0</c>: 1 attempt total, dead-letter on first failure</description></item>
    ///   <item><description><c>MaxRetries = 3</c> (default): 4 attempts total</description></item>
    ///   <item><description><c>MaxRetries = 100</c>: 101 attempts total</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Retries are immediate (no backoff). For transient failure handling with
    /// exponential backoff, use <c>.WithResilience()</c> instead.
    /// </para>
    /// </remarks>
    [Range(0, 100)]
    public int MaxRetries { get; set; } = 3;
}