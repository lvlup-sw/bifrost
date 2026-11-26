// =============================================================================
// <copyright file="ResilientWorkHandler.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Handlers;

using Microsoft.Extensions.Logging;

using Polly;
using Polly.Retry;

namespace Bifrost.Resilience;

/// <summary>
/// Handler decorator that runs each work item through a Polly v8 pipeline: opt-in retries outside a
/// cooperative per-attempt timeout.
/// </summary>
/// <typeparam name="TWork">The type of work item to handle.</typeparam>
/// <remarks>
/// <para>
/// <c>WithHandlerResilience</c> registers this decorator in the reserved handler decorator band, so
/// it is the outermost handler decorator. Each attempt calls the inner chain again: every user
/// handler decorator sees each attempt, and a scoped handler gets a fresh DI scope per attempt,
/// which is disposed before the retry delay starts. The orchestrator's attempt loop and terminal
/// outcome stage run outside this decorator, and the worker and key slot are held until the last
/// attempt exits.
/// </para>
/// <para>
/// A failed attempt is retried only when retries remain, the handler token passed in is not
/// cancelled, and <see cref="HandlerResilienceOptions.ShouldRetry"/> returns <see langword="true"/>.
/// So an <see cref="OperationCanceledException"/> from host shutdown or <c>AbortAsync</c> is never
/// retried. The final exception propagates unchanged.
/// </para>
/// <para>
/// The decorator counts each attempt through <see cref="IAttemptCountingHandler{TWork}"/>, so the
/// orchestrator's outcome and attempt metrics include the retries. The pipeline is built once. Each
/// call rents a <see cref="ResilienceContext"/> from the shared pool and passes its state without a
/// closure.
/// </para>
/// </remarks>
internal sealed class ResilientWorkHandler<TWork> : IAttemptCountingHandler<TWork>
{
    private readonly IWorkHandler<TWork> _inner;
    private readonly ResiliencePipeline _pipeline;
    private readonly ILogger _logger;
    private readonly Func<Exception, bool>? _shouldRetry;

    /// <summary>Creates the decorator and builds its pipeline.</summary>
    /// <param name="inner">The inner handler chain.</param>
    /// <param name="options">The validated handler resilience settings.</param>
    /// <param name="logger">The logger for retries and predicate failures.</param>
    /// <param name="timeProvider">The time source for timeouts and retry delays.</param>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    internal ResilientWorkHandler(
        IWorkHandler<TWork> inner,
        HandlerResilienceOptions options,
        ILogger logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _inner = inner;
        _logger = logger;
        _shouldRetry = options.ShouldRetry;
        _pipeline = BuildPipeline(options, timeProvider);
    }

    /// <inheritdoc/>
    public ValueTask HandleAsync(TWork work, CancellationToken ct) => HandleAsync(work, new HandlerAttemptCounter(), ct);

    /// <inheritdoc/>
    public async ValueTask HandleAsync(TWork work, HandlerAttemptCounter attempts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        var context = ResilienceContextPool.Shared.Get(ct);
        try
        {
            await _pipeline.ExecuteAsync(
                static (attemptContext, state) =>
                {
                    state.Attempts.Increment();
                    return state.Inner.HandleAsync(state.Work, attemptContext.CancellationToken);
                },
                context,
                (Inner: _inner, Work: work, Attempts: attempts));
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>Builds the pipeline: retry (when enabled) outside the per-attempt timeout (when set).</summary>
    /// <remarks>
    /// Polly requires at least one retry for a retry strategy, so zero retries leave the strategy
    /// out. With no timeout and no retries the pipeline is empty and runs one attempt. Every retry
    /// delay, including a doubled one, is capped at <see cref="HandlerResilienceOptions.MaxRetryDelay"/>.
    /// </remarks>
    private ResiliencePipeline BuildPipeline(HandlerResilienceOptions options, TimeProvider timeProvider)
    {
        var builder = new ResiliencePipelineBuilder { TimeProvider = timeProvider };
        if (options.MaxRetries > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = options.MaxRetries,
                Delay = options.RetryDelay,
                MaxDelay = HandlerResilienceOptions.MaxRetryDelay,
                BackoffType = options.UseExponentialBackoff ? DelayBackoffType.Exponential : DelayBackoffType.Constant,
                UseJitter = false,
                ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome.Exception, args.Context.CancellationToken)),
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        args.Outcome.Exception,
                        "Handler attempt {Attempt} for work type {WorkType} failed with a transient error. Retrying in {RetryDelay}.",
                        args.AttemptNumber + 1,
                        typeof(TWork).Name,
                        args.RetryDelay);
                    return ValueTask.CompletedTask;
                },
            });
        }

        if (options.AttemptTimeout is { } timeout)
        {
            builder.AddTimeout(timeout);
        }

        return builder.Build();
    }

    /// <summary>Returns whether a failed attempt may be retried.</summary>
    /// <param name="error">The attempt's exception, or <see langword="null"/> when the attempt succeeded.</param>
    /// <param name="outerToken">The orchestrator's handler token.</param>
    /// <returns><see langword="true"/> when the failure is classified as transient and the handler token is not cancelled.</returns>
    /// <remarks>
    /// The outer token is checked before the predicate, so a failure after host shutdown or abort is
    /// never retried and the predicate is not called. A predicate that throws is logged and treated
    /// as "not transient", so the original failure is kept.
    /// </remarks>
    private bool IsTransient(Exception? error, CancellationToken outerToken)
    {
        if (error is null || outerToken.IsCancellationRequested || _shouldRetry is null)
            return false;

        try
        {
            return _shouldRetry(error);
        }
        catch (Exception predicateError)
        {
            _logger.LogError(
                predicateError,
                "The handler resilience ShouldRetry predicate for work type {WorkType} threw. The failure is not retried.",
                typeof(TWork).Name);
            return false;
        }
    }
}
