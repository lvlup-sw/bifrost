// =============================================================================
// <copyright file="TestThreadPoolInitializer.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Runtime.CompilerServices;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Raises the process-wide thread-pool floor once, at test-assembly load, before any test runs.
/// </summary>
/// <remarks>
/// The thread-churn proofs (e.g. <c>ThreadChurnTests.Operations_OnThreadPoolWithAsyncYields_*</c>)
/// fan out dozens of pool tasks that <c>await Task.Yield()</c> between queue operations and then
/// assert the continuations spanned <i>more than one</i> pool-thread <c>ThreadHandle</c> — that is
/// the whole point: it exercises the per-operation handle (and stuck-stickiness) re-fetch path.
/// Under TUnit's default parallelism, amplified by the <c>--coverage</c> instrumentation overhead on
/// CI's few-core runners, the runtime's thread-injection heuristic (which adds worker threads only
/// ~1–2 per second) can serialize every continuation onto a single pool thread, so only one distinct
/// handle is observed and the assertion intermittently fails. Pre-seeding a higher minimum gives the
/// pool enough threads for continuations to land on distinct threads promptly. This touches only the
/// thread-pool <i>minimum</i> (the pool still grows on demand) and affects test execution alone —
/// never product code. Mirrors <c>Bifrost.Tests.TestThreadPoolInitializer</c>.
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
