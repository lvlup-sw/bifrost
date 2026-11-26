// =============================================================================
// <copyright file="HandlerDecoratorOrders.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.DependencyInjection;

/// <summary>Handler decorator orders that Bifrost reserves for its own handler stages.</summary>
/// <remarks>
/// <para>
/// Handler decorators are applied in ascending order, so a higher order is further out. Orders at
/// or above <see cref="ReservedBandStart"/> are reserved. <c>WithHandlerDecorator</c> rejects them,
/// and its automatic order ignores them, so every user decorator stays inside every reserved stage
/// for every builder call order.
/// </para>
/// <para>
/// The full handler chain, from the inside out, is: the scoped handler proxy (a fresh DI scope per
/// call) or the singleton handler, then user decorators, then the reserved band, and then the
/// orchestrator's fixed attempt loop and terminal outcome stage (dead-letter retries and capture).
/// </para>
/// </remarks>
internal static class HandlerDecoratorOrders
{
    /// <summary>The first reserved handler decorator order.</summary>
    internal const int ReservedBandStart = 1_000_000_000;

    /// <summary>The order of handler resilience (<c>WithHandlerResilience</c>), the outermost handler stage.</summary>
    internal const int HandlerResilience = ReservedBandStart;

    /// <summary>Returns whether <paramref name="order"/> is in the reserved band.</summary>
    /// <param name="order">The handler decorator order.</param>
    /// <returns><see langword="true"/> when the order is reserved.</returns>
    internal static bool IsReserved(int order) => order >= ReservedBandStart;
}
