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
    /// The <paramref name="binding"/> is set before the delegate is invoked, so
    /// <paramref name="configure"/> may inspect or override it as an escape hatch.
    /// </param>
    /// <param name="binding">
    /// The binding intent for the priority queue. <see cref="PriorityBinding.Auto"/>
    /// (the default) always resolves to the lock-free MultiQueue — it never selects
    /// locking. <see cref="PriorityBinding.Locking"/> is the explicit opt-in for the
    /// coarse-locking heap (exact ordering, the by-construction starvation bound);
    /// it is the only way to get locking, since <see cref="PriorityBinding.Auto"/>
    /// keeps relaxed ordering even at high capacity (where the MultiQueue's expected
    /// rank error grows). <see cref="PriorityBinding.MultiQueue"/> forces the
    /// lock-free MultiQueue (relaxed ordering, higher producer concurrency). See the
    /// 600 s soak results (<c>docs/benchmarks/2026-06-cpq-soak.md</c>) for the regime
    /// analysis.
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
    /// construction — no reflective resolution (trim/AOT-safe). The resolved
    /// binding is logged once at construction and exposed via
    /// <c>WorkOrchestrator.ResolvedBinding</c>.
    /// </para>
    /// </remarks>
    public static WorkOrchestratorBuilder<TWork> UsePriorityDispatch<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Action<PriorityDispatchOptions>? configure = null,
        PriorityBinding binding = PriorityBinding.Auto)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.Configure<WorkOrchestratorOptions>(options =>
        {
            options.DispatchStrategy = DispatchStrategy.Priority;
            options.Priority.Binding = binding;
            configure?.Invoke(options.Priority);
        });

        return builder;
    }
}
