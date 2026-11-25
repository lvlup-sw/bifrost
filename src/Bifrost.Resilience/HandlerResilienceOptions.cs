// =============================================================================
// <copyright file="HandlerResilienceOptions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Resilience;

/// <summary>
/// Settings for handler resilience (<c>WithHandlerResilience</c>): a cooperative timeout for each
/// handler attempt and short, opt-in retries for failures the caller classifies as transient.
/// </summary>
/// <remarks>
/// <para>
/// The defaults add no timeout and no retries, so each item runs one attempt. Turn on what you need:
/// set <see cref="AttemptTimeout"/> for a per-attempt timeout, and set <see cref="MaxRetries"/>
/// together with <see cref="ShouldRetry"/> for retries.
/// </para>
/// <para>
/// A worker and, with keyed dispatch, the item's key slot stay held for every attempt, every retry
/// delay, and every scope disposal. That is why retries are short and delays are capped: a long
/// wait blocks the key. Work that needs long or durable retries belongs in a durable runtime that
/// submits a fresh attempt.
/// </para>
/// <para>
/// The settings are named options for each work type and are validated when the orchestrator is
/// resolved and on host start.
/// </para>
/// </remarks>
public sealed class HandlerResilienceOptions
{
    /// <summary>The largest allowed value of <see cref="MaxRetries"/>.</summary>
    public const int MaxRetriesLimit = 10;

    /// <summary>Gets the largest allowed value of <see cref="RetryDelay"/>.</summary>
    public static TimeSpan MaxRetryDelay { get; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets the smallest allowed value of <see cref="AttemptTimeout"/>.</summary>
    public static TimeSpan MinAttemptTimeout { get; } = TimeSpan.FromMilliseconds(10);

    /// <summary>Gets the largest allowed value of <see cref="AttemptTimeout"/>.</summary>
    public static TimeSpan MaxAttemptTimeout { get; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Gets or sets the timeout for each handler attempt, or <see langword="null"/> (the default)
    /// for no timeout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The timeout is cooperative. When it expires, the cancellation token passed to the handler is
    /// cancelled. A handler that observes the token ends the attempt, which then fails with Polly's
    /// <c>Polly.Timeout.TimeoutRejectedException</c>. A timed-out item is recorded as failed, not
    /// canceled, and it is retried only when <see cref="ShouldRetry"/> returns <see langword="true"/>
    /// for that exception.
    /// </para>
    /// <para>
    /// A handler that ignores the token keeps running. Its worker and key slot stay held until it
    /// exits, and the attempt's own result is then used.
    /// </para>
    /// <para>The value must be between 10 milliseconds and one day.</para>
    /// </remarks>
    public TimeSpan? AttemptTimeout { get; set; }

    /// <summary>
    /// Gets or sets the number of retries after the first attempt. The default is 0, which turns
    /// retries off.
    /// </summary>
    /// <remarks>
    /// The value must be between 0 and <see cref="MaxRetriesLimit"/>. A value above 0 requires
    /// <see cref="ShouldRetry"/>. Total attempts are at most <c>1 + MaxRetries</c>.
    /// </remarks>
    public int MaxRetries { get; set; }

    /// <summary>Gets or sets the delay before the first retry. The default is 200 milliseconds.</summary>
    /// <remarks>
    /// The value must be between zero and <see cref="MaxRetryDelay"/>. With
    /// <see cref="UseExponentialBackoff"/>, each later delay doubles. The worker and key slot stay
    /// held during the delay.
    /// </remarks>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Gets or sets a value indicating whether retry delays double after each retry. The default is
    /// <see langword="false"/>, which keeps the delay constant.
    /// </summary>
    public bool UseExponentialBackoff { get; set; }

    /// <summary>
    /// Gets or sets the predicate that classifies a failed attempt's exception as transient. A
    /// transient failure is retried while retries remain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bifrost does not guess which failures are safe to repeat. Return <see langword="true"/> only
    /// for failures that are safe to retry, for example when the handler's side effects are
    /// idempotent.
    /// </para>
    /// <para>
    /// The predicate is not called, and nothing is retried, once the orchestrator's handler token is
    /// cancelled (for example by host shutdown or <c>AbortAsync</c>). A predicate that throws is
    /// treated as "not transient": the error is logged and the original failure is kept.
    /// </para>
    /// </remarks>
    public Func<Exception, bool>? ShouldRetry { get; set; }
}
