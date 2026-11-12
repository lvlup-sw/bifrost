// =============================================================================
// <copyright file="DeadLetterNotifier.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core.Events;

using Microsoft.Extensions.Logging;

namespace Bifrost.DeadLetter;

/// <summary>
/// Multi-subscriber notifier for dead letter events. Subscribers are invoked
/// fire-and-forget on the thread pool with per-subscriber error isolation.
/// Callbacks can overlap and must return promptly. Notifications are best-effort
/// and are not replayed. At most <see cref="MaxOutstandingCallbacks"/> deliveries
/// run at once; further notifications are dropped. Disposing the notifier stops
/// new deliveries and skips callbacks that have not started.
/// </summary>
/// <typeparam name="TWork">The type of work item.</typeparam>
internal sealed class DeadLetterNotifier<TWork> : IDisposable
{
    internal const int MaxOutstandingCallbacks = 32;

    private readonly List<Func<WorkDeadLetteredEvent<TWork>, Task>> _callbacks = [];
    private readonly SemaphoreSlim _outstanding = new(MaxOutstandingCallbacks, MaxOutstandingCallbacks);
    private readonly object _lock = new();
    private readonly ILogger<DeadLetterNotifier<TWork>> _logger;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeadLetterNotifier{TWork}"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public DeadLetterNotifier(ILogger<DeadLetterNotifier<TWork>> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Stops new notifications. Callbacks that have not started are skipped.
    /// </summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }

    /// <summary>
    /// Notifies all subscribers that a work item was dead-lettered.
    /// Subscribers are invoked fire-and-forget; exceptions in individual
    /// subscribers do not propagate to the caller or affect other subscribers.
    /// </summary>
    /// <param name="evt">The dead-lettered event.</param>
    public void Notify(WorkDeadLetteredEvent<TWork> evt)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        List<Func<WorkDeadLetteredEvent<TWork>, Task>> snapshot;
        lock (_lock)
        {
            if (_callbacks.Count == 0) return;
            snapshot = [.. _callbacks];
        }

        foreach (var callback in snapshot)
        {
            // A slow subscriber must not schedule unbounded thread-pool work.
            if (!_outstanding.Wait(0))
            {
                _logger.LogWarning(
                    "Dropped a dead letter notification because {Limit} callbacks are still outstanding",
                    MaxOutstandingCallbacks);
                continue;
            }

            // Fire-and-forget to avoid blocking the handler loop.
            // Errors are logged but don't affect the main processing path.
            _ = Task.Run(() => DeliverBoundedAsync(callback, evt));
        }
    }

    /// <summary>
    /// Subscribes to dead letter notifications with a synchronous callback.
    /// Multiple subscribers are supported.
    /// </summary>
    /// <param name="callback">The callback to invoke when a work item is dead-lettered.</param>
    /// <returns>A disposable that removes the subscription when disposed.</returns>
    public IDisposable Subscribe(Action<WorkDeadLetteredEvent<TWork>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        // Wrap Action in a Func<,Task> to unify storage
        Func<WorkDeadLetteredEvent<TWork>, Task> wrapper = evt =>
        {
            callback(evt);
            return Task.CompletedTask;
        };

        return AddCallback(wrapper);
    }

    /// <summary>
    /// Subscribes to dead letter notifications with an async callback.
    /// Multiple subscribers are supported.
    /// </summary>
    /// <param name="callback">The async callback to invoke when a work item is dead-lettered.</param>
    /// <returns>A disposable that removes the subscription when disposed.</returns>
    public IDisposable SubscribeAsync(Func<WorkDeadLetteredEvent<TWork>, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        return AddCallback(callback);
    }

    private IDisposable AddCallback(Func<WorkDeadLetteredEvent<TWork>, Task> callback)
    {
        lock (_lock)
        {
            _callbacks.Add(callback);
        }

        return new Subscription(this, callback);
    }

    private async Task DeliverBoundedAsync(
        Func<WorkDeadLetteredEvent<TWork>, Task> callback,
        WorkDeadLetteredEvent<TWork> evt)
    {
        try
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                await NotifySafe(callback, evt).ConfigureAwait(false);
            }
        }
        finally
        {
            _outstanding.Release();
        }
    }

    private async Task NotifySafe(
        Func<WorkDeadLetteredEvent<TWork>, Task> callback,
        WorkDeadLetteredEvent<TWork> evt)
    {
        try
        {
            await callback(evt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dead letter subscriber threw an exception for work type {WorkType}", typeof(TWork).Name);
        }
    }

    private sealed class Subscription(
        DeadLetterNotifier<TWork> notifier,
        Func<WorkDeadLetteredEvent<TWork>, Task> callback) : IDisposable
    {
        public void Dispose()
        {
            lock (notifier._lock)
            {
                notifier._callbacks.Remove(callback);
            }
        }
    }
}
