// =============================================================================
// <copyright file="ScopeResolvingJobDispatcher.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;

using Bifrost.Scheduling.Core;

using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Scheduling.Dispatch;

/// <summary>
/// Resolves <typeparamref name="TDispatcher"/> from the fire's service scope on
/// every fire. A singleton registration is shared; a scoped or transient
/// registration is created for that fire and disposed with the scope. Startup
/// does not resolve the dispatcher from the root provider.
/// </summary>
/// <typeparam name="TDispatcher">The dispatcher type registered in DI.</typeparam>
internal sealed class ScopeResolvingJobDispatcher<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TDispatcher>
    : IJobDispatcher
    where TDispatcher : class, IJobDispatcher
{
    private readonly IServiceProvider root;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScopeResolvingJobDispatcher{TDispatcher}"/> class.
    /// </summary>
    /// <param name="root">The application provider used when a fire has no scope.</param>
    public ScopeResolvingJobDispatcher(IServiceProvider root)
    {
        ArgumentNullException.ThrowIfNull(root);
        this.root = root;
    }

    /// <inheritdoc/>
    public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
    {
        var dispatcher = context.Services.GetService<TDispatcher>()
            ?? this.root.GetService<TDispatcher>()
            ?? throw new InvalidOperationException(
                $"Job dispatcher '{typeof(TDispatcher).Name}' is not available for this fire.");

        return dispatcher.DispatchAsync(context, ct);
    }
}
