// =============================================================================
// <copyright file="DispatchAdmission.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Dispatch;

/// <summary>
/// Marks the async flow of a running job dispatch.
/// A trigger issued from that flow must not wait for a slot the dispatch itself is holding.
/// </summary>
internal static class DispatchAdmission
{
    private static readonly AsyncLocal<int> Depth = new();

    /// <summary>True when the current async flow is inside a job dispatch.</summary>
    internal static bool IsReentrant => Depth.Value > 0;

    /// <summary>Enters a dispatch flow until the returned scope is disposed.</summary>
    /// <returns>The scope that leaves the dispatch flow.</returns>
    internal static IDisposable Enter()
    {
        Depth.Value++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose()
        {
            var next = Depth.Value - 1;
            Depth.Value = next < 0 ? 0 : next;
        }
    }
}
