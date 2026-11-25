// =============================================================================
// <copyright file="TaskDelegateWorkHandler.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

namespace Bifrost.Handlers;

/// <summary>
/// Work handler that wraps a <see cref="Task"/>-returning delegate for processing work items.
/// </summary>
/// <typeparam name="TWork">The type of work item to handle.</typeparam>
/// <remarks>
/// <para>
/// Used internally by the <c>WithTaskHandler(Func&lt;TWork, CancellationToken, Task&gt;)</c>
/// builder method to wrap a user-provided delegate as an <see cref="IWorkHandler{TWork}"/>.
/// The returned <see cref="Task"/> is wrapped in a <see cref="ValueTask"/> without an extra
/// async state machine.
/// </para>
/// <para>
/// A delegate that returns a <see langword="null"/> <see cref="Task"/> breaks the handler contract,
/// so <see cref="HandleAsync"/> throws <see cref="InvalidOperationException"/> instead of letting a
/// <see cref="NullReferenceException"/> surface later in the worker.
/// </para>
/// </remarks>
internal sealed class TaskDelegateWorkHandler<TWork> : IWorkHandler<TWork>
{
    private readonly Func<TWork, CancellationToken, Task> _handler;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskDelegateWorkHandler{TWork}"/> class.
    /// </summary>
    /// <param name="handler">The delegate to invoke for each work item.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="handler"/> is null.</exception>
    public TaskDelegateWorkHandler(Func<TWork, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Thrown when the delegate returns a null <see cref="Task"/>.</exception>
    public ValueTask HandleAsync(TWork work, CancellationToken ct)
    {
        var task = _handler(work, ct) ?? throw new InvalidOperationException(
            $"The handler delegate registered with WithTaskHandler for work type '{typeof(TWork).Name}' " +
            "returned a null Task. Return a Task (for example Task.CompletedTask) instead of null.");

        return new ValueTask(task);
    }
}
