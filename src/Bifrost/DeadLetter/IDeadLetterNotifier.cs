// =============================================================================
// <copyright file="IDeadLetterNotifier.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core.Events;

namespace Bifrost.DeadLetter;

/// <summary>
/// Provides notification when work items are dead-lettered.
/// </summary>
/// <typeparam name="TWork">The type of work item.</typeparam>
internal interface IDeadLetterNotifier<TWork>
{
    /// <summary>
    /// Notifies the subscriber that a work item was dead-lettered.
    /// </summary>
    /// <param name="evt">The dead-lettered event.</param>
    void Notify(WorkDeadLetteredEvent<TWork> evt);

    /// <summary>
    /// Subscribes to dead letter notifications.
    /// </summary>
    /// <param name="callback">The callback to invoke when a work item is dead-lettered.</param>
    /// <returns>A disposable that removes the subscription when disposed.</returns>
    IDisposable Subscribe(Action<WorkDeadLetteredEvent<TWork>> callback);
}