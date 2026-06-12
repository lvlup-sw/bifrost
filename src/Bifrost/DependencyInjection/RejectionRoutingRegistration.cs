// =============================================================================
// <copyright file="RejectionRoutingRegistration.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.DeadLetter;
using Bifrost.Decorators;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bifrost.DependencyInjection;

/// <summary>
/// Shared, idempotent registration of the
/// <see cref="RejectionRoutingOrchestrator{TWork}"/> decorator (DR-6). Both
/// <c>WithDeadLetterQueue</c> (which supplies the dead-letter services the
/// decorator routes to) and <c>WithOpenTelemetry</c> (which attaches the
/// <c>bifrost.orchestrator.rejected</c> counter to it — rejections are always
/// counted, independent of DLQ configuration) ensure this registration, in any
/// call order; the factory resolves the DLQ services optionally at build time.
/// </summary>
internal static class RejectionRoutingRegistration
{
    /// <summary>
    /// Decorator order for the rejection-routing decorator: the innermost
    /// wrapping decorator, directly above the bare
    /// <see cref="WorkOrchestrator{TWork}"/>, so it observes raw admission
    /// outcomes before resilience (25), event stream (50), and autoscaling (100)
    /// apply — and below the OpenTelemetry queue-wait attach pass-through
    /// (<c>int.MinValue</c>), which must still receive the bare orchestrator.
    /// </summary>
    internal const int DecoratorOrder = 10;

    /// <summary>
    /// Registers the rejection-routing decorator at <see cref="DecoratorOrder"/>
    /// unless a registration at that order already exists (idempotent across
    /// repeated and cross-extension calls).
    /// </summary>
    /// <typeparam name="TWork">The type of work item to process.</typeparam>
    /// <param name="builder">The work orchestrator builder.</param>
    internal static void EnsureRegistered<TWork>(WorkOrchestratorBuilder<TWork> builder)
    {
        if (builder.Decorators.Any(d => d.Order == DecoratorOrder))
        {
            return;
        }

        // The factory closure captures 'builder' so that EventPublishCallback is
        // read at factory execution time during Build(), allowing WithEventStream
        // to be called in any order — mirroring the DeadLetterHandler wiring.
        builder.Decorators.Add(new DecoratorRegistration<TWork>(
            DecoratorOrder,
            (sp, orchestrator) => new RejectionRoutingOrchestrator<TWork>(
                orchestrator,
                sp.GetService<IDeadLetterQueue<TWork>>(),
                sp.GetService<DeadLetterNotifier<TWork>>(),
                sp.GetRequiredService<ILogger<RejectionRoutingOrchestrator<TWork>>>(),
                builder.EventPublishCallback)));
    }
}
