// =============================================================================
// <copyright file="DelegateWorkHandler.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

namespace Bifrost.Handlers;

/// <summary>
/// Work handler that executes delegate work items.
/// </summary>
/// <remarks>
/// <para>
/// Provides a migration path from <c>Func&lt;Task&gt;</c>-based orchestrators to the
/// <see cref="IWorkHandler{TWork}"/> pattern.
/// </para>
/// <para>
/// This handler is useful when you have existing code that uses delegates for work items
/// and want to integrate with the <see cref="IWorkOrchestrator{TWork}"/> infrastructure.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Register delegate-based orchestrator
/// services.AddWorkOrchestrator&lt;Func&lt;Task&gt;&gt;()
///     .WithHandler&lt;DelegateWorkHandler&gt;()
///     .Build();
///
/// // Enqueue work
/// await orchestrator.EnqueueAsync(async () =&gt; {
///     await DoWorkAsync();
/// });
/// </code>
/// </example>
public sealed class DelegateWorkHandler : IWorkHandler<Func<Task>>
{
    /// <summary>
    /// Executes the delegate work item.
    /// </summary>
    /// <param name="work">The delegate to execute.</param>
    /// <param name="ct">Cancellation token (not passed to the delegate).</param>
    /// <returns>A <see cref="ValueTask"/> that completes when the delegate finishes.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="work"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// The cancellation token is not passed to the delegate since <c>Func&lt;Task&gt;</c>
    /// does not accept parameters. Use <see cref="CancellableDelegateWorkHandler"/> if you
    /// need cancellation support.
    /// </para>
    /// </remarks>
    public async ValueTask HandleAsync(Func<Task> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        await work().ConfigureAwait(false);
    }
}
