// =============================================================================
// <copyright file="DeadLetterNotifier.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core.Events;

namespace Bifrost.DeadLetter;

/// <summary>
/// Single-subscriber notifier for dead letter events.
/// </summary>
/// <typeparam name="TWork">The type of work item.</typeparam>
internal sealed class DeadLetterNotifier<TWork>
{
    private Action<WorkDeadLetteredEvent<TWork>>? _callback;

    /// <summary>
    /// Notifies the subscriber that a work item was dead-lettered.
    /// </summary>
    /// <param name="evt">The dead-lettered event.</param>
    public void Notify(WorkDeadLetteredEvent<TWork> evt)
    {
        _callback?.Invoke(evt);
    }

    /// <summary>
    /// Subscribes to dead letter notifications. Only one active subscriber is allowed.
    /// </summary>
    /// <param name="callback">The callback to invoke when a work item is dead-lettered.</param>
    /// <returns>A disposable that removes the subscription when disposed.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a subscriber is already active.</exception>
    public IDisposable Subscribe(Action<WorkDeadLetteredEvent<TWork>> callback)
    {
        if (Interlocked.CompareExchange(ref _callback, callback, null) != null)
        {
            throw new InvalidOperationException("DeadLetterNotifier already has an active subscriber.");
        }

        return new Subscription(this);
    }

    private sealed class Subscription(DeadLetterNotifier<TWork> notifier) : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Exchange(ref notifier._callback, null);
        }
    }
}
