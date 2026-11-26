// =============================================================================
// <copyright file="KeyedDispatchExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Queues;

using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.DependencyInjection;

/// <summary>Configures keyed admission and dispatch.</summary>
public static class KeyedDispatchExtensions
{
    /// <summary>
    /// Groups work by a key and limits, for each key, how many items may wait and how many may run
    /// at the same time. Ready keys share the workers round robin.
    /// </summary>
    /// <typeparam name="TWork">The work type.</typeparam>
    /// <typeparam name="TKey">The work key type, for example a tenant and session tuple.</typeparam>
    /// <param name="builder">The builder.</param>
    /// <param name="keySelector">
    /// Returns the key of a work item. It runs once per admission attempt, on the caller's thread,
    /// so keep it cheap. It must not return <see langword="null"/>.
    /// </param>
    /// <param name="maxPendingPerKey">
    /// The most items of one key that may be queued and not yet started. Running items are not
    /// counted. Must be at least 1.
    /// </param>
    /// <param name="maxConcurrencyPerKey">
    /// The most items of one key that may run at the same time. Use 1 to run each key's items one
    /// at a time in admission order. Must be at least 1.
    /// </param>
    /// <param name="comparer">The key comparer, or <see langword="null"/> for the default comparer.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="keySelector"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a limit is less than 1.</exception>
    /// <example>
    /// <code>
    /// services.AddWorkOrchestrator&lt;Turn&gt;()
    ///     .WithKeyedDispatch(
    ///         static turn => (turn.TenantId, turn.SessionId),
    ///         maxPendingPerKey: 8,
    ///         maxConcurrencyPerKey: 1)
    ///     .WithScopedHandler(HandleTurnAsync)
    ///     .Build();
    /// </code>
    /// </example>
    /// <remarks>
    /// <para>
    /// <b>Admission.</b> Admission returns at once. When
    /// <see cref="WorkOrchestratorOptions.Capacity"/> items are queued across all keys, it rejects
    /// with <see cref="RejectionReason.CapacityExceeded"/>. When the key already has
    /// <paramref name="maxPendingPerKey"/> queued items, it rejects with
    /// <see cref="RejectionReason.KeyLimitExceeded"/>. When both limits are full, the global limit
    /// wins. A <paramref name="maxPendingPerKey"/> larger than the capacity is allowed: the global
    /// limit then always applies first. Key-limit rejections are not dead-lettered.
    /// </para>
    /// <para>
    /// <b>Ordering.</b> Each key keeps its items in admission order. A worker reserves a key slot
    /// and takes the key's oldest item in one step, and frees the slot after the whole handler
    /// pipeline exits, including dead-letter retries and the disposal of a scoped handler's scope.
    /// A key at its concurrency limit keeps its items queued, and other keys use the free workers.
    /// With a limit above 1, items start in admission order and can finish in any order. Keys with
    /// no queued and no running items are removed.
    /// </para>
    /// <para>
    /// <b>Configuration.</b> The method sets <see cref="WorkOrchestratorOptions.DispatchStrategy"/>
    /// to <see cref="DispatchStrategy.Keyed"/> and <see cref="WorkOrchestratorOptions.FullMode"/>
    /// to <see cref="QueueFullMode.Reject"/>, so <c>EnqueueAsync</c> never waits for space. Keyed
    /// dispatch cannot be combined with class priority: <c>UsePriorityDispatch</c> or
    /// <c>WithClassifier</c> on the same builder makes
    /// <see cref="WorkOrchestratorBuilder{TWork}.Build"/> throw, and a priority strategy or
    /// <see cref="QueueFullMode.Wait"/> set through options fails options validation on start.
    /// A per-item <see cref="WorkEnqueueOptions.Class"/> is accepted and carried with the item,
    /// but it does not change the order. A second call replaces the first.
    /// </para>
    /// <para>
    /// <b>Autoscaling.</b> With <c>WithAutoscaling</c>, scale-up counts only the items a free
    /// worker could take now. Items queued behind a key at its concurrency limit do not add
    /// workers. Scale-down, capacity, and drain still use the total pending count, so a blocked
    /// backlog does not remove the workers that drain it either.
    /// </para>
    /// <para>The limits apply to one orchestrator instance, not across processes.</para>
    /// </remarks>
    public static WorkOrchestratorBuilder<TWork> WithKeyedDispatch<TWork, TKey>(
        this WorkOrchestratorBuilder<TWork> builder,
        Func<TWork, TKey> keySelector,
        int maxPendingPerKey,
        int maxConcurrencyPerKey,
        IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPendingPerKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrencyPerKey);

        builder.KeyedQueueFactory = (options, _) => KeyedWorkQueue.Create(
            keySelector, options.Capacity, maxPendingPerKey, maxConcurrencyPerKey, comparer);
        builder.Services.Configure<WorkOrchestratorOptions>(WorkOptions.Name<TWork>(), static options =>
        {
            if (options.DispatchStrategy == DispatchStrategy.Fifo)
                options.DispatchStrategy = DispatchStrategy.Keyed;
            options.FullMode = QueueFullMode.Reject;
        });
        return builder;
    }
}
