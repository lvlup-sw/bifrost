// =============================================================================
// <copyright file="ResiliencyPolicyGenerator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Data.Common;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;

using Polly;
using Polly.Timeout;
using Polly.Wrap;

namespace Bifrost.Resilience;

/// <summary>
/// Provides utility methods to generate resiliency policies for asynchronous operations using Polly.
/// </summary>
/// <remarks>
/// <para>
/// This class is designed to create a combination of resiliency policies such as retry,
/// circuit breaker, timeout, bulkhead isolation, and fallback.
/// </para>
/// <para>
/// It simplifies the configuration of policies based on provided settings and ensures
/// consistent error handling across the work orchestrator.
/// </para>
/// </remarks>
public static class ResiliencyPolicyGenerator
{
    /// <summary>
    /// Creates an asynchronous policy wrap based on the provided settings.
    /// </summary>
    /// <typeparam name="T">The return type of the operation being protected.</typeparam>
    /// <param name="logger">Logger instance to use for policy logging.</param>
    /// <param name="settings">The configured resiliency settings.</param>
    /// <param name="fallbackValue">The fallback value to return if all policies fail.</param>
    /// <param name="handledExceptionTypes">Specific exception types to handle. If null or empty, handles transient exceptions.</param>
    /// <returns>A configured <see cref="AsyncPolicyWrap{TResult}"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when logger or settings is null.</exception>
    /// <remarks>
    /// <para>
    /// The fallback policy is the outermost layer — it executes last, after all retries and
    /// circuit breaker attempts are exhausted. When triggered, the fallback logs an error via
    /// <see cref="ILogger"/> and returns <paramref name="fallbackValue"/>.
    /// </para>
    /// <para>
    /// <strong>Important:</strong> Callers cannot distinguish a successful result (possibly after
    /// retries) from a fallback value. If distinguishing these cases is required, callers should
    /// wrap the result in a discriminated type or use <see cref="GetAsyncBackgroundTaskPattern"/>
    /// which allows exceptions to propagate.
    /// </para>
    /// </remarks>
    public static AsyncPolicyWrap<T> GeneratePolicy<T>(
        ILogger logger,
        ResiliencySettings settings,
        T fallbackValue,
        params Type[]? handledExceptionTypes)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(settings);

        var exceptionPredicate = CreateExceptionPredicate(handledExceptionTypes);

        return GetResiliencyPattern(logger, settings, fallbackValue, exceptionPredicate);
    }

    /// <summary>
    /// Gets the transient exception types that should be handled by resilience policies.
    /// </summary>
    /// <returns>An array of exception types that represent transient failures.</returns>
    /// <remarks>
    /// <para>Transient exceptions include:</para>
    /// <list type="bullet">
    ///   <item><description><see cref="HttpRequestException"/> - Network communication failures</description></item>
    ///   <item><description><see cref="TimeoutException"/> - Operation timeouts</description></item>
    ///   <item><description><see cref="SocketException"/> - Low-level network failures</description></item>
    ///   <item><description><see cref="IOException"/> - File system or stream failures</description></item>
    ///   <item><description><see cref="DbException"/> - Database connectivity issues</description></item>
    ///   <item><description><see cref="InvalidOperationException"/> - Process execution failures</description></item>
    ///   <item><description><see cref="TimeoutRejectedException"/> - Polly timeout policy failures</description></item>
    /// </list>
    /// </remarks>
    public static Type[] GetTransientExceptionTypes() =>
    [
        typeof(HttpRequestException),
        typeof(TimeoutException),
        typeof(SocketException),
        typeof(IOException),
        typeof(DbException),
        typeof(InvalidOperationException),
        typeof(TimeoutRejectedException)
    ];

    /// <summary>
    /// Creates a predicate function to check if an exception matches the specified types.
    /// </summary>
    /// <param name="handledExceptionTypes">The specific exception types to handle. Uses transient exception types if null or empty.</param>
    /// <returns>A function that returns true if the exception should be handled.</returns>
    internal static Func<Exception, bool> CreateExceptionPredicate(Type[]? handledExceptionTypes)
    {
        if (handledExceptionTypes is null || handledExceptionTypes.Length == 0)
        {
            var transientTypes = GetTransientExceptionTypes();
            return ex => transientTypes.Any(type => type.IsAssignableFrom(ex.GetType()));
        }

        return ex => handledExceptionTypes.Any(type => type.IsAssignableFrom(ex.GetType()));
    }

    /// <summary>
    /// Creates a pre-configured Polly resilience strategy for background tasks.
    /// </summary>
    /// <param name="logger">The logger instance for logging policy events.</param>
    /// <param name="settings">Configuration settings for the policies.</param>
    /// <param name="exceptionPredicate">A function that determines which exceptions to handle.</param>
    /// <returns>An <see cref="AsyncPolicyWrap"/> for background task resilience.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
    /// <remarks>
    /// <para>
    /// This method provides a standard resilience pipeline for background tasks by wrapping
    /// several Polly policies together. Unlike <see cref="GeneratePolicy{T}"/>, this pattern
    /// allows exceptions to propagate after all retries are exhausted.
    /// </para>
    /// <para>The policies are combined in the following order (from outermost to innermost):</para>
    /// <list type="number">
    ///   <item><description><b>Retry:</b> Re-executes the operation if it fails with a handled exception.</description></item>
    ///   <item><description><b>Timeout:</b> Applies a time limit to each attempt.</description></item>
    /// </list>
    /// </remarks>
    public static AsyncPolicyWrap GetAsyncBackgroundTaskPattern(
        ILogger logger,
        ResiliencySettings settings,
        Func<Exception, bool> exceptionPredicate)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(exceptionPredicate);

        // First layer: timeouts
        var timeoutPolicy = Policy.TimeoutAsync(
            TimeSpan.FromSeconds(settings.TimeoutIntervalSeconds),
            TimeoutStrategy.Optimistic);

        // Second layer: automatic retries
        var retryPolicy = Policy
            .Handle(exceptionPredicate)
            .WaitAndRetryAsync(
                retryCount: settings.RetryCount,
                sleepDurationProvider: retryAttempt => CalculateRetryDelay(settings, retryAttempt),
                onRetryAsync: (exception, timeSpan, retryCount, context) =>
                {
                    LogRetryAttempt(logger, settings, exception, timeSpan, retryCount, context);
                    return Task.CompletedTask;
                });

        // Wrap policies in order (outermost first)
        return Policy.WrapAsync(retryPolicy, timeoutPolicy);
    }

    /// <summary>
    /// Creates a pre-configured Polly resilience strategy combining multiple policies.
    /// </summary>
    private static AsyncPolicyWrap<T> GetResiliencyPattern<T>(
        ILogger logger,
        ResiliencySettings settings,
        T fallbackValue,
        Func<Exception, bool> exceptionPredicate)
    {
        // First layer: timeouts
        var timeoutPolicy = Policy.TimeoutAsync(
            TimeSpan.FromSeconds(settings.TimeoutIntervalSeconds),
            TimeoutStrategy.Optimistic);

        // Second layer: bulkhead isolation
        var bulkheadPolicy = Policy.BulkheadAsync(
             settings.BulkheadMaxParallelization,
             settings.BulkheadMaxQueuingActions,
             onBulkheadRejectedAsync: context =>
             {
                 logger.LogWarning("Bulkhead rejected execution. OperationKey: {OperationKey}", context.OperationKey);
                 return Task.CompletedTask;
             });

        // Third layer: automatic retries
        var retryPolicy = Policy
            .Handle(exceptionPredicate)
            .WaitAndRetryAsync(
                retryCount: settings.RetryCount,
                sleepDurationProvider: retryAttempt => CalculateRetryDelay(settings, retryAttempt),
                onRetryAsync: (exception, timeSpan, retryCount, context) =>
                {
                    LogRetryAttempt(logger, settings, exception, timeSpan, retryCount, context);
                    return Task.CompletedTask;
                });

        // Fourth layer: circuit breaker
        var circuitBreakerPolicy = Policy
            .Handle(exceptionPredicate)
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: settings.CircuitBreakerCount,
                durationOfBreak: TimeSpan.FromMinutes(settings.CircuitBreakerIntervalMinutes),
                onBreak: (ex, breakDelay, context) =>
                    logger.LogError(ex, "Circuit breaker opened for {BreakDelay}. OperationKey: {OperationKey}", breakDelay, context.OperationKey),
                onReset: context => logger.LogDebug("Circuit breaker reset. OperationKey: {OperationKey}", context.OperationKey),
                onHalfOpen: () => logger.LogDebug("Circuit breaker half-open."));

        // Final layer: return default value
        var fallbackPolicy = Policy<T>
            .Handle(exceptionPredicate)
            .FallbackAsync(
                fallbackValue: fallbackValue,
                onFallbackAsync: (exception, context) =>
                {
                    logger.LogError(exception.Exception, "Fallback policy executed for OperationKey: {OperationKey}. Returning fallback value.", context.OperationKey);
                    return Task.CompletedTask;
                });

        // Wrap policies in order (outermost first)
        var combinedPolicy = Policy.WrapAsync(
            circuitBreakerPolicy,
            retryPolicy,
            bulkheadPolicy,
            timeoutPolicy);

        // Ensure fallback is the outermost policy (executed last)
        return fallbackPolicy.WrapAsync(combinedPolicy);
    }

    /// <summary>
    /// Calculates the delay TimeSpan for the next retry attempt.
    /// </summary>
    private static TimeSpan CalculateRetryDelay(ResiliencySettings settings, int retryAttempt)
    {
        // Calculate base delay
        TimeSpan baseDelay = settings.UseExponentialBackoff
            ? TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))
            : TimeSpan.FromSeconds(settings.RetryIntervalSeconds);

        // Add jitter: random +/- 0-100ms
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(-100, 100));
        var calculatedDelay = baseDelay + jitter;

        // Ensure delay is not negative
        return calculatedDelay > TimeSpan.Zero
            ? calculatedDelay
            : TimeSpan.FromMilliseconds(100);
    }

    /// <summary>
    /// Logs information about a retry attempt.
    /// </summary>
    private static void LogRetryAttempt(
        ILogger logger,
        ResiliencySettings settings,
        Exception exception,
        TimeSpan timeSpan,
        int retryCount,
        Context context)
    {
        bool isFinalAttempt = retryCount == settings.RetryCount;

        var logLevel = isFinalAttempt
            ? LogLevel.Error
            : LogLevel.Warning;

        string message = isFinalAttempt
            ? $"Retry limit ({settings.RetryCount}) reached for OperationKey: {context.OperationKey}. Final exception before fallback/circuit breaker."
            : $"Retry {retryCount} of {settings.RetryCount} for OperationKey: {context.OperationKey}. Delay: {timeSpan.TotalSeconds:N2}s. Reason:";

        logger.Log(logLevel, exception, message);
    }
}