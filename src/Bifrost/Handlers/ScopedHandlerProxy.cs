// =============================================================================
// <copyright file="ScopedHandlerProxy.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Handlers;

/// <summary>
/// Handler proxy that creates a new DI scope per work item, resolving
/// <see cref="IWorkHandler{TWork}"/> from the scoped service provider.
/// </summary>
/// <typeparam name="TWork">The type of work item to handle.</typeparam>
/// <remarks>
/// <para>
/// This proxy is used when handlers are registered with <see cref="ServiceLifetime.Scoped"/>
/// to ensure each work item is processed within its own DI scope. This is critical for
/// scoped services like EF Core <c>DbContext</c> that must not be shared across work items.
/// </para>
/// <para>
/// The proxy itself is registered as a singleton and creates async scopes on each invocation.
/// The scope is disposed after the handler completes, even if an exception occurs.
/// </para>
/// </remarks>
internal sealed class ScopedHandlerProxy<TWork> : IWorkHandler<TWork>
{
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScopedHandlerProxy{TWork}"/> class.
    /// </summary>
    /// <param name="scopeFactory">The service scope factory for creating per-item scopes.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="scopeFactory"/> is null.</exception>
    public ScopedHandlerProxy(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc/>
    public async ValueTask HandleAsync(TWork work, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IWorkHandler<TWork>>();
        await handler.HandleAsync(work, ct).ConfigureAwait(false);
    }
}
