// =============================================================================
// <copyright file="ClassifierExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

namespace Bifrost.DependencyInjection;

/// <summary>
/// Extension methods for configuring an options-level work classifier on the
/// work orchestrator (DR-2).
/// </summary>
public static class ClassifierExtensions
{
    /// <summary>
    /// Configures a work classifier delegate, the options-level alternative to
    /// tagging each enqueue with a <see cref="WorkClass"/>. The delegate is
    /// threaded to the orchestrator constructor and consulted by every enqueue
    /// overload.
    /// </summary>
    /// <typeparam name="TWork">The type of work item to process.</typeparam>
    /// <param name="builder">The work orchestrator builder.</param>
    /// <param name="classifier">
    /// The classifier mapping a work item to its <see cref="WorkClass"/>.
    /// </param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when builder or classifier is null.</exception>
    /// <remarks>
    /// <para>
    /// Precedence rule (DR-2): a per-call class other than
    /// <see cref="WorkClass.Default"/> wins; a per-call
    /// <see cref="WorkClass.Default"/> defers to the classifier; with no classifier
    /// the class stays <see cref="WorkClass.Default"/>. The classifier is invoked
    /// only when the per-call class is <see cref="WorkClass.Default"/>, keeping the
    /// default FIFO hot path allocation-free.
    /// </para>
    /// <para>
    /// The classifier runs synchronously on the enqueue path — keep it cheap and
    /// exception-free; it must not block.
    /// </para>
    /// </remarks>
    public static WorkOrchestratorBuilder<TWork> WithClassifier<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Func<TWork, WorkClass> classifier)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(classifier);

        builder.Classifier = classifier;

        return builder;
    }
}
