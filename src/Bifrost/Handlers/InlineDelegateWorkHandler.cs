// =============================================================================
// <copyright file="InlineDelegateWorkHandler.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

namespace Bifrost.Handlers;

/// <summary>
/// Work handler that wraps an inline delegate for processing work items.
/// </summary>
/// <typeparam name="TWork">The type of work item to handle.</typeparam>
/// <remarks>
/// <para>
/// Used internally by the <c>WithHandler(Func&lt;TWork, CancellationToken, ValueTask&gt;)</c>
/// builder overload to wrap a user-provided delegate as an <see cref="IWorkHandler{TWork}"/>.
/// </para>
/// </remarks>
internal sealed class InlineDelegateWorkHandler<TWork> : IWorkHandler<TWork>
{
    private readonly Func<TWork, CancellationToken, ValueTask> _handler;

    /// <summary>
    /// Initializes a new instance of the <see cref="InlineDelegateWorkHandler{TWork}"/> class.
    /// </summary>
    /// <param name="handler">The delegate to invoke for each work item.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="handler"/> is null.</exception>
    public InlineDelegateWorkHandler(Func<TWork, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    /// <inheritdoc/>
    public ValueTask HandleAsync(TWork work, CancellationToken ct)
    {
        return _handler(work, ct);
    }
}
