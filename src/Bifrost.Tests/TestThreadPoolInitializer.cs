// =============================================================================
// <copyright file="TestThreadPoolInitializer.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Runtime.CompilerServices;

namespace Bifrost.Tests;

/// <summary>
/// Raises the process-wide thread-pool floor once, at test-assembly load, before any test runs.
/// </summary>
/// <remarks>
/// This suite is async- and orchestrator-heavy: many tests spin up <c>WorkOrchestrator</c> workers
/// and event-stream subscriber tasks concurrently. Under TUnit's default parallelism — amplified by
/// the <c>--coverage</c> instrumentation overhead on CI's few-core runners — the runtime's
/// thread-injection heuristic (which adds worker threads only ~1–2 per second) leaves continuations
/// queued for seconds, intermittently tripping the integration tests' async timeouts (a different
/// test each run). Pre-seeding a higher minimum removes the injection stall so those waits complete
/// promptly. This touches only the thread-pool <i>minimum</i> (the pool still grows on demand) and
/// affects test execution alone — never product code.
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
        ThreadPool.SetMinThreads(target, Math.Max(completionPortThreads, target));
    }
}
