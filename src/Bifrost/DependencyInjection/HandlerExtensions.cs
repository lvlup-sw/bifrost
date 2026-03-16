// =============================================================================
// <copyright file="HandlerExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;

using Bifrost.Core;
using Bifrost.Handlers;

using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.DependencyInjection;

/// <summary>
/// Extension methods for registering work handlers with the orchestrator builder.
/// </summary>
public static class HandlerExtensions
{
    /// <summary>
    /// Registers a work handler by type with the specified service lifetime.
    /// </summary>
    /// <typeparam name="TWork">The type of work item to process.</typeparam>
    /// <typeparam name="THandler">The handler implementation type.</typeparam>
    /// <param name="builder">The builder to configure.</param>
    /// <param name="lifetime">
    /// The service lifetime for the handler registration. Default is <see cref="ServiceLifetime.Singleton"/>.
    /// Use <see cref="ServiceLifetime.Scoped"/> for handlers that depend on scoped services (e.g., EF Core DbContext).
    /// </param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a handler has already been registered.</exception>
    /// <remarks>
    /// <para>
    /// When <paramref name="lifetime"/> is <see cref="ServiceLifetime.Scoped"/>, the builder
    /// will use a <c>ScopedHandlerProxy</c> that creates a new DI scope per work item,
    /// ensuring scoped services like <c>DbContext</c> are properly isolated.
    /// </para>
    /// </remarks>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2087:DynamicallyAccessedMemberTypes",
        Justification = "THandler is a concrete type known at compile time")]
    public static WorkOrchestratorBuilder<TWork> WithHandler<TWork, THandler>(
        this WorkOrchestratorBuilder<TWork> builder,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where THandler : class, IWorkHandler<TWork>
    {
        ArgumentNullException.ThrowIfNull(builder);
        ValidateHandlerNotRegistered(builder);

        builder.Services.Add(ServiceDescriptor.Describe(
            typeof(IWorkHandler<TWork>),
            typeof(THandler),
            lifetime));

        builder.HandlerRegistered = true;
        builder.HandlerLifetime = lifetime;

        return builder;
    }

    /// <summary>
    /// Registers a work handler using a factory function with the specified service lifetime.
    /// </summary>
    /// <typeparam name="TWork">The type of work item to process.</typeparam>
    /// <param name="builder">The builder to configure.</param>
    /// <param name="factory">Factory function that creates the handler from the service provider.</param>
    /// <param name="lifetime">
    /// The service lifetime for the handler registration. Default is <see cref="ServiceLifetime.Singleton"/>.
    /// </param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="factory"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a handler has already been registered.</exception>
    public static WorkOrchestratorBuilder<TWork> WithHandler<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Func<IServiceProvider, IWorkHandler<TWork>> factory,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);
        ValidateHandlerNotRegistered(builder);

        builder.Services.Add(new ServiceDescriptor(
            typeof(IWorkHandler<TWork>),
            sp => factory(sp),
            lifetime));

        builder.HandlerRegistered = true;
        builder.HandlerLifetime = lifetime;

        return builder;
    }

    /// <summary>
    /// Registers a work handler from an inline delegate, wrapped in an <see cref="InlineDelegateWorkHandler{TWork}"/>.
    /// </summary>
    /// <typeparam name="TWork">The type of work item to process.</typeparam>
    /// <param name="builder">The builder to configure.</param>
    /// <param name="handler">The delegate invoked for each work item.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="handler"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a handler has already been registered.</exception>
    /// <remarks>
    /// <para>
    /// The delegate is wrapped in a singleton <see cref="InlineDelegateWorkHandler{TWork}"/>.
    /// For scoped or transient lifetimes, use the factory overload or type-based overload instead.
    /// </para>
    /// </remarks>
    public static WorkOrchestratorBuilder<TWork> WithHandler<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Func<TWork, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(handler);
        ValidateHandlerNotRegistered(builder);

        var wrappedHandler = new InlineDelegateWorkHandler<TWork>(handler);

        builder.Services.AddSingleton<IWorkHandler<TWork>>(wrappedHandler);

        builder.HandlerRegistered = true;
        builder.HandlerLifetime = ServiceLifetime.Singleton;

        return builder;
    }

    /// <summary>
    /// Validates that a handler has not already been registered on the builder.
    /// </summary>
    /// <typeparam name="TWork">The type of work item.</typeparam>
    /// <param name="builder">The builder to validate.</param>
    /// <exception cref="InvalidOperationException">Thrown when a handler has already been registered.</exception>
    private static void ValidateHandlerNotRegistered<TWork>(WorkOrchestratorBuilder<TWork> builder)
    {
        if (builder.HandlerRegistered)
        {
            throw new InvalidOperationException(
                $"A handler has already been registered for work type '{typeof(TWork).Name}'. " +
                "WithHandler can only be called once per builder.");
        }
    }
}
