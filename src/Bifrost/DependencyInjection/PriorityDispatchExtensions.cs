// =============================================================================
// <copyright file="PriorityDispatchExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.DependencyInjection;

/// <summary>
/// Extension methods for enabling class-based priority dispatch on the work
/// orchestrator (DR-4, DR-6).
/// </summary>
public static class PriorityDispatchExtensions
{
    /// <summary>
    /// Selects a priority dispatch strategy for the work orchestrator instead of
    /// the default strict-FIFO queue.
    /// </summary>
    /// <typeparam name="TWork">The type of work item to process.</typeparam>
    /// <param name="builder">The work orchestrator builder.</param>
    /// <param name="configure">
    /// Optional configuration of the <see cref="PriorityDispatchOptions"/> — boost
    /// windows and admission watermarks. When null, the documented defaults apply.
    /// </param>
    /// <param name="useLockingBinding">
    /// When <c>true</c>, selects <see cref="DispatchStrategy.PriorityLocking"/>
    /// (coarse-locking heap: exact ordering and admission boundaries); when
    /// <c>false</c> (default), selects
    /// <see cref="DispatchStrategy.PriorityMultiQueue"/> (lock-free MultiQueue:
    /// relaxed ordering, higher producer concurrency). Indicative soak measurements
    /// (<c>docs/benchmarks/2026-06-cpq-soak.md</c>) currently favor the locking
    /// binding in the 1–8-worker, seconds-long regime.
    /// </param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when builder is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>This changes the asynchronous enqueue semantics (DR-6):</b> the priority
    /// strategies are FAIL-FAST at admission — <c>EnqueueAsync</c> never waits for
    /// space; at capacity or above the work class's admission watermark it rejects
    /// immediately with <see cref="RejectionReason.CapacityExceeded"/> (the two
    /// causes are deliberately indistinguishable; rejections route to the
    /// dead-letter queue when configured). The FIFO default's producer-wait
    /// behavior is unchanged.
    /// </para>
    /// <para>
    /// Strategy selection is enum/factory-based and applied at orchestrator
    /// construction — no reflective resolution (trim/AOT-safe).
    /// </para>
    /// </remarks>
    public static WorkOrchestratorBuilder<TWork> UsePriorityDispatch<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Action<PriorityDispatchOptions>? configure = null,
        bool useLockingBinding = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var strategy = useLockingBinding
            ? DispatchStrategy.PriorityLocking
            : DispatchStrategy.PriorityMultiQueue;

        builder.Services.Configure<WorkOrchestratorOptions>(options =>
        {
            options.DispatchStrategy = strategy;
            configure?.Invoke(options.Priority);
        });

        return builder;
    }
}
