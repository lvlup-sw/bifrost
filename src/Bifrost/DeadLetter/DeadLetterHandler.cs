// =============================================================================
// <copyright file="DeadLetterHandler.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.Core.Events;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost.DeadLetter;

/// <summary>
/// Handler decorator that retries failed work and routes to a dead letter queue after exhaustion.
/// </summary>
/// <typeparam name="TWork">The type of work item.</typeparam>
internal sealed class DeadLetterHandler<TWork> : IWorkHandler<TWork>
{
    private readonly IWorkHandler<TWork> _inner;
    private readonly IDeadLetterQueue<TWork> _dlq;
    private readonly DeadLetterNotifier<TWork> _notifier;
    private readonly int _maxRetries;
    private readonly ILogger<DeadLetterHandler<TWork>> _logger;
    private readonly Action<IOrchestratorEvent>? _eventPublishCallback;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeadLetterHandler{TWork}"/> class.
    /// </summary>
    /// <param name="inner">The inner handler to delegate work to.</param>
    /// <param name="dlq">The dead letter queue for failed items.</param>
    /// <param name="notifier">The notifier for dead letter events.</param>
    /// <param name="options">The dead letter queue options.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="eventPublishCallback">Optional callback to publish dead-letter events to the event stream.</param>
    public DeadLetterHandler(
        IWorkHandler<TWork> inner,
        IDeadLetterQueue<TWork> dlq,
        DeadLetterNotifier<TWork> notifier,
        IOptions<DeadLetterQueueOptions> options,
        ILogger<DeadLetterHandler<TWork>> logger,
        Action<IOrchestratorEvent>? eventPublishCallback = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(dlq);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _dlq = dlq;
        _notifier = notifier;
        _maxRetries = options.Value.MaxRetries;
        _logger = logger;
        _eventPublishCallback = eventPublishCallback;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// The handler makes one initial attempt plus <c>MaxRetries</c> retries. The retries are
    /// immediate (no backoff) by design: the dead-letter path is for persistent failures, not for
    /// transient network problems. A caller that needs backoff must implement it in the inner
    /// handler. A cancellation of <paramref name="ct"/> is never dead-lettered. It propagates.
    /// </para>
    /// <para>
    /// When all attempts are exhausted, the item is dead-lettered with
    /// <see cref="CancellationToken.None"/>. This guarantees the dead-letter persistence, also when
    /// the caller cancels.
    /// </para>
    /// </remarks>
    public async ValueTask HandleAsync(TWork work, CancellationToken ct)
    {
        var attempts = 0;
        Exception? lastException = null;

        var maxAttempts = _maxRetries + 1;

        for (var i = 0; i < maxAttempts; i++)
        {
            try
            {
                attempts++;
                await _inner.HandleAsync(work, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogWarning(ex, "Work item failed on attempt {Attempt}/{MaxAttempts}", attempts, maxAttempts);
            }
        }

        var deadLetteredWork = new DeadLetteredWork<TWork>(
            work,
            lastException,
            attempts,
            DateTimeOffset.UtcNow,
            CorrelationId: null);

        await _dlq.EnqueueAsync(deadLetteredWork, CancellationToken.None);

        var evt = new WorkDeadLetteredEvent<TWork>(
            work,
            lastException,
            attempts,
            DateTimeOffset.UtcNow);

        _notifier.Notify(evt);

        try
        {
            _eventPublishCallback?.Invoke(evt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish dead-letter event for work type {WorkType}", typeof(TWork).Name);
        }

        _logger.LogError(lastException, "Work item dead-lettered after {Attempts} attempts", attempts);
    }
}
