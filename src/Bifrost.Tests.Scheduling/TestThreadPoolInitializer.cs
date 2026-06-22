// =============================================================================
// <copyright file="TestThreadPoolInitializer.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Runtime.CompilerServices;

namespace Bifrost.Tests.Scheduling;

/// <summary>
/// Raises the process-wide thread-pool floor once, at test-assembly load, before any test runs.
/// </summary>
/// <remarks>
/// The tick-engine tests start a <c>ScheduleTickLoop</c> <c>BackgroundService</c> per fixture and
/// run them under TUnit's default parallelism. Each loop's wake — a <c>FakeTimeProvider</c> timer
/// firing or a pool-thread dispatch completing — resumes a continuation that needs a pool thread.
/// Under the runtime's slow thread-injection heuristic (amplified by <c>--coverage</c> on few-core
/// runners) those continuations can queue for seconds, stalling the deterministic idle handshake.
/// Pre-seeding a higher minimum removes the injection stall. This touches only the thread-pool
/// <i>minimum</i> (the pool still grows on demand) and affects test execution alone.
/// </remarks>
internal static class TestThreadPoolInitializer
{
    /// <summary>
    /// Sets the minimum worker/IO thread counts to a generous multiple of the core count.
    /// </summary>
    [ModuleInitializer]
    internal static void RaiseThreadPoolFloor()
    {
        ThreadPool.GetMinThreads(out int workerThreads, out int completionPortThreads);
        int target = Math.Max(workerThreads, Environment.ProcessorCount * 8);
        if (!ThreadPool.SetMinThreads(target, Math.Max(completionPortThreads, target)))
        {
            // A failed thread-pool floor must not pass silently: the tests rely on this
            // minimum to avoid injection stalls under --coverage on few-core runners.
            throw new InvalidOperationException(
                $"Failed to raise the thread-pool floor to {target} worker/IO threads.");
        }
    }
}
