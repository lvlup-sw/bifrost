// =============================================================================
// <copyright file="ScopedDelegateWorkHandler.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

namespace Bifrost.Handlers;

/// <summary>
/// Work handler that wraps a scope-aware delegate together with the service provider of the
/// DI scope it was resolved from.
/// </summary>
/// <typeparam name="TWork">The type of work item to handle.</typeparam>
/// <remarks>
/// <para>
/// Used internally by the <c>WithScopedHandler(Func&lt;IServiceProvider, TWork, CancellationToken, ValueTask&gt;)</c>
/// builder method. The adapter is registered with the scoped lifetime, so
/// <see cref="ScopedHandlerProxy{TWork}"/> resolves one instance from each per-item async scope.
/// The captured <see cref="IServiceProvider"/> is that scope's provider, and the proxy disposes the
/// scope after <see cref="HandleAsync"/> completes or throws.
/// </para>
/// </remarks>
internal sealed class ScopedDelegateWorkHandler<TWork> : IWorkHandler<TWork>
{
    private readonly IServiceProvider _services;
    private readonly Func<IServiceProvider, TWork, CancellationToken, ValueTask> _handler;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScopedDelegateWorkHandler{TWork}"/> class.
    /// </summary>
    /// <param name="services">The service provider of the scope this handler belongs to.</param>
    /// <param name="handler">The delegate to invoke for each work item.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="handler"/> is null.
    /// </exception>
    public ScopedDelegateWorkHandler(
        IServiceProvider services,
        Func<IServiceProvider, TWork, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(handler);
        _services = services;
        _handler = handler;
    }

    /// <inheritdoc/>
    public ValueTask HandleAsync(TWork work, CancellationToken ct)
    {
        return _handler(_services, work, ct);
    }
}
