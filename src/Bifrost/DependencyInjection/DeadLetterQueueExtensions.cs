// =============================================================================
// <copyright file="DeadLetterQueueExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;

using Bifrost.Core.DeadLetter;
using Bifrost.DeadLetter;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost.DependencyInjection;

/// <summary>
/// Extension methods for adding dead letter queue support to the work orchestrator.
/// </summary>
public static class DeadLetterQueueExtensions
{
    /// <summary>
    /// Adds dead letter queue support to the work orchestrator.
    /// </summary>
    /// <typeparam name="TWork">The type of work item to process.</typeparam>
    /// <param name="builder">The work orchestrator builder.</param>
    /// <param name="configure">Optional configuration action for dead letter queue options.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <remarks>
    /// <para>
    /// This extension registers the following services:
    /// <list type="bullet">
    ///   <item><description><see cref="IDeadLetterQueue{TWork}"/> - Channel-backed dead letter queue</description></item>
    ///   <item><description><see cref="IDeadLetterNotifier{TWork}"/> - Single-subscriber notification bridge</description></item>
    ///   <item><description><see cref="DeadLetterHandler{TWork}"/> - Handler decorator for retry and DLQ routing</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "DeadLetterQueueOptions type is fully preserved and known at compile time")]
    public static WorkOrchestratorBuilder<TWork> WithDeadLetterQueue<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Action<DeadLetterQueueOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Register options with DataAnnotations validation
        builder.Services.AddOptions<DeadLetterQueueOptions>()
            .Configure(configure ?? (_ => { }))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Register DLQ services (TryAdd to avoid duplicates)
        builder.Services.TryAddSingleton<IDeadLetterQueue<TWork>>(sp =>
            new DeadLetterQueue<TWork>(sp.GetRequiredService<IOptions<DeadLetterQueueOptions>>()));

        builder.Services.TryAddSingleton<IDeadLetterNotifier<TWork>, DeadLetterNotifier<TWork>>();

        // Add handler decorator to wrap the handler with retry+DLQ logic
        builder.HandlerDecorators.Add(new HandlerDecoratorRegistration<TWork>(
            Order: builder.HandlerDecorators.Count,
            Factory: (sp, handler) =>
                new DeadLetterHandler<TWork>(
                    handler,
                    sp.GetRequiredService<IDeadLetterQueue<TWork>>(),
                    sp.GetRequiredService<IDeadLetterNotifier<TWork>>(),
                    sp.GetRequiredService<IOptions<DeadLetterQueueOptions>>(),
                    sp.GetRequiredService<ILogger<DeadLetterHandler<TWork>>>())));

        return builder;
    }
}
