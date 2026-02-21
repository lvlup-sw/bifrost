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
    private readonly IDeadLetterNotifier<TWork> _notifier;
    private readonly int _maxRetries;
    private readonly ILogger<DeadLetterHandler<TWork>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeadLetterHandler{TWork}"/> class.
    /// </summary>
    /// <param name="inner">The inner handler to delegate work to.</param>
    /// <param name="dlq">The dead letter queue for failed items.</param>
    /// <param name="notifier">The notifier for dead letter events.</param>
    /// <param name="options">The dead letter queue options.</param>
    /// <param name="logger">The logger instance.</param>
    public DeadLetterHandler(
        IWorkHandler<TWork> inner,
        IDeadLetterQueue<TWork> dlq,
        IDeadLetterNotifier<TWork> notifier,
        IOptions<DeadLetterQueueOptions> options,
        ILogger<DeadLetterHandler<TWork>> logger)
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
    }

    /// <inheritdoc/>
    public async ValueTask HandleAsync(TWork work, CancellationToken ct)
    {
        var attempts = 0;
        Exception? lastException = null;

        // 1 initial attempt + MaxRetries retries
        var maxAttempts = _maxRetries + 1;

        for (var i = 0; i < maxAttempts; i++)
        {
            try
            {
                attempts++;
                await _inner.HandleAsync(work, ct).ConfigureAwait(false);
                return; // Success
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Never dead-letter cancellations
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogWarning(ex, "Work item failed on attempt {Attempt}/{MaxAttempts}", attempts, maxAttempts);
            }
        }

        // All attempts exhausted - dead-letter
        var deadLetteredWork = new DeadLetteredWork<TWork>(
            work,
            lastException,
            attempts,
            DateTimeOffset.UtcNow,
            CorrelationId: null);

        await _dlq.EnqueueAsync(deadLetteredWork, ct).ConfigureAwait(false);

        var evt = new WorkDeadLetteredEvent<TWork>(
            work,
            lastException,
            attempts,
            DateTimeOffset.UtcNow);

        _notifier.Notify(evt);

        _logger.LogError(lastException, "Work item dead-lettered after {Attempts} attempts", attempts);
    }
}