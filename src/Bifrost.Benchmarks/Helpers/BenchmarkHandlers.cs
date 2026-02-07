// =============================================================================
// <copyright file="BenchmarkHandlers.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

namespace Bifrost.Benchmarks.Helpers;

/// <summary>
/// A no-op work handler that completes immediately. Used as a baseline in benchmarks.
/// </summary>
internal sealed class NoOpWorkHandler : IWorkHandler<int>
{
    public ValueTask HandleAsync(int work, CancellationToken ct) => default;
}

/// <summary>
/// A work handler that signals a <see cref="CountdownEvent"/> on each item processed.
/// </summary>
internal sealed class CountdownWorkHandler(CountdownEvent countdown) : IWorkHandler<int>
{
    public ValueTask HandleAsync(int work, CancellationToken ct)
    {
        countdown.Signal();
        return default;
    }
}
