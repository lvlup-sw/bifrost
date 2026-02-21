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
internal sealed class DeadLetterNotifier<TWork> : IDeadLetterNotifier<TWork>
{
    private Action<WorkDeadLetteredEvent<TWork>>? _callback;

    /// <inheritdoc/>
    public void Notify(WorkDeadLetteredEvent<TWork> evt)
    {
        _callback?.Invoke(evt);
    }

    /// <inheritdoc/>
    public IDisposable Subscribe(Action<WorkDeadLetteredEvent<TWork>> callback)
    {
        _callback = callback;
        return new Subscription(this);
    }

    private sealed class Subscription(DeadLetterNotifier<TWork> notifier) : IDisposable
    {
        public void Dispose()
        {
            notifier._callback = null;
        }
    }
}