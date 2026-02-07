// =============================================================================
// <copyright file="CancellableDelegateWorkHandler.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

namespace Bifrost.Handlers;

/// <summary>
/// Work handler that executes delegate work items with cancellation support.
/// </summary>
/// <remarks>
/// <para>
/// Provides a migration path from <c>Func&lt;CancellationToken, Task&gt;</c>-based orchestrators
/// to the <see cref="IWorkHandler{TWork}"/> pattern.
/// </para>
/// <para>
/// This handler passes the cancellation token to the delegate, allowing proper
/// cooperative cancellation of work items.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Register cancellable delegate-based orchestrator
/// services.AddWorkOrchestrator&lt;Func&lt;CancellationToken, Task&gt;&gt;()
///     .WithHandler&lt;CancellableDelegateWorkHandler&gt;()
///     .Build();
///
/// // Enqueue work with cancellation support
/// await orchestrator.EnqueueAsync(async ct =&gt; {
///     await DoWorkAsync(ct);
/// });
/// </code>
/// </example>
public sealed class CancellableDelegateWorkHandler : IWorkHandler<Func<CancellationToken, Task>>
{
    /// <summary>
    /// Executes the delegate work item with cancellation support.
    /// </summary>
    /// <param name="work">The delegate to execute.</param>
    /// <param name="ct">Cancellation token to pass to the delegate.</param>
    /// <returns>A <see cref="ValueTask"/> that completes when the delegate finishes.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="work"/> is null.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the delegate observes cancellation.</exception>
    /// <remarks>
    /// <para>
    /// The cancellation token is passed directly to the delegate, enabling proper
    /// cooperative cancellation. The delegate is responsible for checking the token
    /// and responding appropriately to cancellation requests.
    /// </para>
    /// <para>
    /// If the delegate does not need cancellation support, consider using
    /// <see cref="DelegateWorkHandler"/> instead, which accepts <c>Func&lt;Task&gt;</c>
    /// delegates without a cancellation token parameter.
    /// </para>
    /// </remarks>
    public async ValueTask HandleAsync(Func<CancellationToken, Task> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        await work(ct).ConfigureAwait(false);
    }
}