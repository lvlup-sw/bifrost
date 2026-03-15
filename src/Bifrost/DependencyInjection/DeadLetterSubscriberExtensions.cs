// =============================================================================
// <copyright file="DeadLetterSubscriberExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;

using Bifrost.Core.DeadLetter;
using Bifrost.Core.Events;
using Bifrost.DeadLetter;

using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.DependencyInjection;

/// <summary>
/// Extension methods for registering dead letter subscribers with the orchestrator builder.
/// </summary>
public static class DeadLetterSubscriberExtensions
{
    /// <summary>
    /// Registers a dead letter subscriber by type. The subscriber is resolved from DI
    /// and wired into the <see cref="DeadLetterNotifier{TWork}"/> via a post-build action.
    /// </summary>
    /// <typeparam name="TWork">The type of work item.</typeparam>
    /// <typeparam name="TSubscriber">The subscriber implementation type.</typeparam>
    /// <param name="builder">The builder to configure.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// The subscriber is registered as a singleton in DI. During build, a post-build action
    /// resolves the <see cref="DeadLetterNotifier{TWork}"/> and the subscriber, then subscribes
    /// the subscriber to dead letter notifications.
    /// </para>
    /// <para>
    /// Requires <c>.WithDeadLetterQueue()</c> to be called on the builder for the notifier
    /// to be available. If the notifier is not registered, resolving the orchestrator will throw.
    /// </para>
    /// </remarks>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2091:DynamicallyAccessedMemberTypes",
        Justification = "TSubscriber is a concrete type known at compile time")]
    public static WorkOrchestratorBuilder<TWork> WithDeadLetterSubscriber<TWork, TSubscriber>(
        this WorkOrchestratorBuilder<TWork> builder)
        where TSubscriber : class, IDeadLetterSubscriber<TWork>
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<TSubscriber>();

        builder.PostBuildActions.Add(sp =>
        {
            var notifier = sp.GetRequiredService<DeadLetterNotifier<TWork>>();
            var subscriber = sp.GetRequiredService<TSubscriber>();
            notifier.SubscribeAsync(async evt =>
            {
                var dlw = ToDeadLetteredWork(evt);
                await subscriber.HandleAsync(dlw, CancellationToken.None).ConfigureAwait(false);
            });
        });

        return builder;
    }

    /// <summary>
    /// Registers a dead letter subscriber using a callback. The callback is wired into
    /// the <see cref="DeadLetterNotifier{TWork}"/> via a post-build action.
    /// </summary>
    /// <typeparam name="TWork">The type of work item.</typeparam>
    /// <param name="builder">The builder to configure.</param>
    /// <param name="callback">The callback invoked when a work item is dead-lettered.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="callback"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// During build, a post-build action resolves the <see cref="DeadLetterNotifier{TWork}"/>
    /// and subscribes the callback to dead letter notifications.
    /// </para>
    /// <para>
    /// Requires <c>.WithDeadLetterQueue()</c> to be called on the builder for the notifier
    /// to be available. If the notifier is not registered, resolving the orchestrator will throw.
    /// </para>
    /// </remarks>
    public static WorkOrchestratorBuilder<TWork> WithDeadLetterSubscriber<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Func<DeadLetteredWork<TWork>, CancellationToken, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        builder.PostBuildActions.Add(sp =>
        {
            var notifier = sp.GetRequiredService<DeadLetterNotifier<TWork>>();
            notifier.SubscribeAsync(async evt =>
            {
                var dlw = ToDeadLetteredWork(evt);
                await callback(dlw, CancellationToken.None).ConfigureAwait(false);
            });
        });

        return builder;
    }

    /// <summary>
    /// Converts a <see cref="WorkDeadLetteredEvent{TWork}"/> to a <see cref="DeadLetteredWork{TWork}"/>.
    /// </summary>
    /// <typeparam name="TWork">The type of work item.</typeparam>
    /// <param name="evt">The dead-lettered event.</param>
    /// <returns>A <see cref="DeadLetteredWork{TWork}"/> representation of the event.</returns>
    private static DeadLetteredWork<TWork> ToDeadLetteredWork<TWork>(WorkDeadLetteredEvent<TWork> evt)
    {
        return new DeadLetteredWork<TWork>(
            evt.Work,
            evt.Exception,
            evt.AttemptCount,
            evt.Timestamp,
            evt.CorrelationId);
    }
}
