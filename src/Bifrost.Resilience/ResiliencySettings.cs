// =============================================================================
// <copyright file="ResiliencySettings.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Resilience;

/// <summary>
/// Resiliency settings for resiliency policy generation.
/// </summary>
/// <remarks>
/// <para>
/// These settings control the behavior of Polly resilience policies including
/// retry, timeout, bulkhead isolation, and circuit breaker patterns.
/// </para>
/// <para>
/// Configure these settings via the Options pattern to customize resilience
/// behavior for the work orchestrator.
/// </para>
/// </remarks>
public class ResiliencySettings
{
    /// <summary>
    /// Configuration section key for resiliency settings.
    /// </summary>
    public static string Key => "ResiliencySettings";

    /// <summary>
    /// Gets or sets the number of times to retry an operation.
    /// </summary>
    /// <value>The retry count. Default is 3.</value>
    public int RetryCount { get; set; } = 3;

    /// <summary>
    /// Gets or sets the interval in seconds between operation retries.
    /// </summary>
    /// <value>The retry interval in seconds. Default is 2.</value>
    public int RetryIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// Gets or sets a value indicating whether to use exponential backoff for operation retries.
    /// </summary>
    /// <value>True to use exponential backoff; otherwise, false. Default is true.</value>
    public bool UseExponentialBackoff { get; set; } = true;

    /// <summary>
    /// Gets or sets the timeout interval in seconds.
    /// </summary>
    /// <value>The timeout in seconds. Default is 5.</value>
    public int TimeoutIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum number of concurrent operations.
    /// </summary>
    /// <value>The maximum parallelization. Default is 10.</value>
    public int BulkheadMaxParallelization { get; set; } = 10;

    /// <summary>
    /// Gets or sets the maximum number of enqueued operations allowed.
    /// </summary>
    /// <value>The maximum queuing actions. Default is 20.</value>
    public int BulkheadMaxQueuingActions { get; set; } = 20;

    /// <summary>
    /// Gets or sets how many exceptions are tolerated before restricting executions.
    /// </summary>
    /// <value>The circuit breaker exception count. Default is 3.</value>
    public int CircuitBreakerCount { get; set; } = 3;

    /// <summary>
    /// Gets or sets the time in minutes before retrying after being restricted.
    /// </summary>
    /// <value>The circuit breaker interval in minutes. Default is 1.</value>
    public int CircuitBreakerIntervalMinutes { get; set; } = 1;
}
