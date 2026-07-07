// =============================================================================
// <copyright file="BifrostHttpResiliencePolicy.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Resilience;

/// <summary>
/// The tunable resilience knobs for a single named <see cref="System.Net.Http.HttpClient"/> policy,
/// applied by <see cref="BifrostHttpResilienceExtensions.AddBifrostResilienceHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the strategy chain of the Microsoft standard resilience handler, but as plain, config-bindable
/// <see cref="TimeSpan"/>/<see cref="int"/> values so every HttpClient's policy can be expressed and
/// overridden purely in configuration. The strategies are ordered from outermost to innermost as:
/// total-request timeout → retry → circuit breaker → per-attempt timeout.
/// </para>
/// <para>
/// Long-running clients tune this instead of bypassing resilience: an SSE stream sets
/// <see cref="MaxRetries"/> to <c>0</c> and a generous <see cref="TotalRequestTimeout"/>; a slow
/// provisioning call (e.g. a cold sandbox microVM start) sets a longer <see cref="AttemptTimeout"/>.
/// </para>
/// </remarks>
public sealed class BifrostHttpResiliencePolicy
{
    /// <summary>
    /// Gets or sets the overall timeout applied to the whole request, including all retry attempts.
    /// The outermost strategy. Default is 30 seconds.
    /// </summary>
    public TimeSpan TotalRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the timeout applied to each individual attempt. The innermost strategy. Should be
    /// less than or equal to <see cref="TotalRequestTimeout"/>. Default is 10 seconds.
    /// </summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the maximum number of retry attempts. Set to <c>0</c> to disable retries entirely
    /// (the retry strategy is then omitted from the pipeline — the correct choice for non-idempotent or
    /// streaming requests). Default is 3.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Gets or sets the base delay between retry attempts (the seed for backoff). Default is 1 second.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets a value indicating whether retries use exponential backoff (versus a constant delay).
    /// Default is <see langword="true"/>.
    /// </summary>
    public bool UseExponentialBackoff { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether jitter is added to retry delays to avoid synchronized
    /// retry storms. Default is <see langword="true"/>.
    /// </summary>
    public bool UseJitter { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the circuit breaker strategy is included. Set to
    /// <see langword="false"/> for single, long-lived connections where a breaker adds no value.
    /// Default is <see langword="true"/>.
    /// </summary>
    public bool EnableCircuitBreaker { get; set; } = true;

    /// <summary>
    /// Gets or sets the failure-to-throughput ratio (0.0–1.0) that opens the circuit within a sampling
    /// window. Default is 0.1 (10%).
    /// </summary>
    public double CircuitBreakerFailureRatio { get; set; } = 0.1;

    /// <summary>
    /// Gets or sets the minimum number of actions within the sampling window before the circuit breaker
    /// can open. Default is 100.
    /// </summary>
    public int CircuitBreakerMinimumThroughput { get; set; } = 100;

    /// <summary>
    /// Gets or sets the rolling window over which the circuit breaker evaluates the failure ratio.
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan CircuitBreakerSamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets how long the circuit stays open before transitioning to half-open. Default is 5 seconds.
    /// </summary>
    public TimeSpan CircuitBreakerBreakDuration { get; set; } = TimeSpan.FromSeconds(5);
}
