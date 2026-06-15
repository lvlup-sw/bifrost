// =============================================================================
// Bifrost.Scheduling.AotSmoke — NativeAOT compatibility smoke test (DR-13)
//
// Exercises four dispatch modes under a NativeAOT-published binary to verify:
//   • No reflection-based job activation
//   • No type-name serialization as an activation key
//   • No expression-tree compilation
//
// The scheduler uses:
//   - no Type.GetType / Activator.CreateInstance — job dispatchers are resolved
//     via generic DI (GetRequiredService<TDispatcher>()) at config time
//   - no Type.FullName as an activation key — JobRecord.DispatcherTypeName is
//     a diagnostic label only (verified by AotSafetyTests)
//   - no Expression.Compile — all dispatch paths use plain async delegates or
//     direct method calls
//
// Beyond *registration*, the smoke test also proves an actual *dispatch* runs
// end-to-end under NativeAOT: two jobs fire near-immediately (a few hundred ms)
// while the tick loop is live — one over the highest-AOT-risk orchestrator path
// and one over the inline path. Each handler sets an AOT-safe signal
// (TaskCompletionSource, no reflection); the program then awaits both with a
// bounded timeout and FAILs if either does not execute. This catches a trimmer
// or AOT regression that breaks the fire path even when registration still
// resolves cleanly.
// =============================================================================

using Bifrost.Core;
using Bifrost.DependencyInjection;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Console.WriteLine("Bifrost.Scheduling AOT smoke — starting.");

// ─────────────────────────────────────────────────────────────────────────────
// 1. Build the host with all four dispatch modes exercised at DI-config time.
// ─────────────────────────────────────────────────────────────────────────────

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Warning);
    })
    .ConfigureServices(services =>
    {
        // Mode 1 (orchestrator) — register IWorkOrchestrator<SmokeWork> via the
        // standard Bifrost DI builder. This is the most generics-heavy / highest
        // AOT-risk dispatcher: GetRequiredService<IWorkOrchestrator<TWork>>() at
        // config time resolves a fully open generic through closed-generic DI
        // without any runtime type lookup or Activator.CreateInstance.
        services.AddWorkOrchestrator<SmokeWork>()
                .WithHandler<SmokeWork, SmokeWorkHandler>()
                .Build();

        // Register a custom dispatcher for mode 4 (resolved by generic DI, not
        // by type name — this is the key AOT-safety proof).
        services.AddSingleton<PrintingDispatcher>();

        services.AddScheduler(b =>
        {
            // Mode 1 — orchestrator dispatch: DispatchTo<IWorkOrchestrator<TWork>>
            // resolves the orchestrator from DI at config time via a fully closed
            // generic GetRequiredService<IWorkOrchestrator<SmokeWork>>() call.
            // This exercises the highest-AOT-risk code path (OrchestratorJobDispatcher<T>
            // + generic DI) without any reflection-based type lookup.
            // Fires ~300 ms out so it actually executes during this run (the tick
            // loop is live after StartAsync). Proves the orchestrator dispatch path
            // runs end-to-end under NativeAOT, not just that it registered.
            b.AddJob<SmokeWork>("smoke-orchestrator")
             .Every(TimeSpan.FromMilliseconds(300))
             .DispatchTo<IWorkOrchestrator<SmokeWork>>(
                 static _ => new SmokeWork("smoke-orchestrator"));

            // Mode 2 — inline dispatch: a delegate runs on the scheduler pool thread.
            // No orchestrator, no reflective activation. Also fires ~300 ms out so
            // we can assert the inline fire path executes under NativeAOT.
            b.AddInlineJob("smoke-inline")
             .Every(TimeSpan.FromMilliseconds(300))
             .Run(static (ctx, ct) =>
             {
                 Console.WriteLine($"[inline] {ctx.JobName} fired at {ctx.FireTime:u}");
                 SmokeSignals.InlineFired.TrySetResult();
                 return ValueTask.CompletedTask;
             });

            // Mode 4 — custom dispatcher resolved from DI at config time via
            // DispatchVia<PrintingDispatcher>() → GetRequiredService<PrintingDispatcher>().
            // No type-name lookup, no Activator.CreateInstance.
            b.AddJob<object>("smoke-custom")
             .Every(TimeSpan.FromHours(24))
             .DispatchVia<PrintingDispatcher>();
        });
    })
    .Build();

// ─────────────────────────────────────────────────────────────────────────────
// 2. Register a job at runtime (after DI is built) — mode 3 (in-memory store).
// ─────────────────────────────────────────────────────────────────────────────

// Start the host (starts SchedulerJobRegistrationService + ScheduleTickLoop).
await host.StartAsync().ConfigureAwait(false);

// Register a runtime job via IScheduleRegistry (uses a no-op dispatcher for
// isolation; the in-memory store persists it).
var registry = host.Services.GetRequiredService<IScheduleRegistry>();
var runtimeDispatcher = new PrintingDispatcher();

await registry.RegisterAsync(
    "smoke-runtime",
    Cadence.Interval(TimeSpan.FromHours(24)),
    MissedFirePolicy.SkipMissed,
    runtimeDispatcher,
    CancellationToken.None).ConfigureAwait(false);

Console.WriteLine("Registered runtime job 'smoke-runtime'.");

// ─────────────────────────────────────────────────────────────────────────────
// 3. Inspect the registry — proves IBifrostScheduleInspector resolves cleanly.
// ─────────────────────────────────────────────────────────────────────────────

var inspector = host.Services.GetRequiredService<IBifrostScheduleInspector>();
var jobs = inspector.GetJobs();

Console.WriteLine($"Registry contains {jobs.Count} job(s):");
foreach (var job in jobs)
{
    Console.WriteLine(
        $"  {job.Name}: state={job.State}, nextFire={job.NextFireAt:u}");
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. Verify all four dispatch modes registered successfully.
// ─────────────────────────────────────────────────────────────────────────────

var jobNames = jobs.Select(j => j.Name).ToHashSet(StringComparer.Ordinal);
if (!jobNames.Contains("smoke-orchestrator"))
{
    Console.Error.WriteLine("FAIL: 'smoke-orchestrator' (mode 1 — orchestrator dispatch) not registered.");
    return 1;
}

if (!jobNames.Contains("smoke-inline"))
{
    Console.Error.WriteLine("FAIL: 'smoke-inline' (mode 2 — inline dispatch) not registered.");
    return 1;
}

if (!jobNames.Contains("smoke-runtime"))
{
    Console.Error.WriteLine("FAIL: 'smoke-runtime' (mode 3 — runtime registration) not registered.");
    return 1;
}

if (!jobNames.Contains("smoke-custom"))
{
    Console.Error.WriteLine("FAIL: 'smoke-custom' (mode 4 — custom dispatcher) not registered.");
    return 1;
}

Console.WriteLine("All four dispatch modes registered and resolved cleanly.");

// ─────────────────────────────────────────────────────────────────────────────
// 5. Prove an actual dispatch EXECUTES under NativeAOT (not just registers).
//    The orchestrator and inline jobs were scheduled ~300 ms out; the tick loop
//    is running, so both should fire. Await both signals with a bounded
//    real-wall-clock timeout. If either does not fire, the fire path is broken
//    under AOT/trim even though registration resolved — FAIL non-zero.
// ─────────────────────────────────────────────────────────────────────────────

var fireTimeout = TimeSpan.FromSeconds(5);

// Task.WaitAsync bounds the await on the externally-completed signal task with a
// real-wall-clock timeout and throws TimeoutException if the job never fires. It
// is AOT-safe (no reflection) and the analyzer-clean idiom for bounding a wait on
// a Task that completes outside this method's flow.
var orchestratorFired = true;
try
{
    await SmokeSignals.OrchestratorHandled.Task.WaitAsync(fireTimeout).ConfigureAwait(false);
    Console.WriteLine("Verified fire: 'smoke-orchestrator (mode 1 — orchestrator dispatch)' executed.");
}
catch (TimeoutException)
{
    orchestratorFired = false;
    Console.Error.WriteLine(
        $"FAIL: 'smoke-orchestrator (mode 1 — orchestrator dispatch)' did not execute within {fireTimeout.TotalSeconds:0.#}s — fire path broken under NativeAOT.");
}

var inlineFired = true;
try
{
    await SmokeSignals.InlineFired.Task.WaitAsync(fireTimeout).ConfigureAwait(false);
    Console.WriteLine("Verified fire: 'smoke-inline (mode 2 — inline dispatch)' executed.");
}
catch (TimeoutException)
{
    inlineFired = false;
    Console.Error.WriteLine(
        $"FAIL: 'smoke-inline (mode 2 — inline dispatch)' did not execute within {fireTimeout.TotalSeconds:0.#}s — fire path broken under NativeAOT.");
}

if (!orchestratorFired || !inlineFired)
{
    // Stop the host so the process terminates deterministically even on failure.
    await host.StopAsync().ConfigureAwait(false);
    return 1;
}

Console.WriteLine("Orchestrator and inline dispatch paths both executed under NativeAOT.");
Console.WriteLine("Bifrost.Scheduling AOT smoke — PASS.");

await host.StopAsync().ConfigureAwait(false);
return 0;

// =============================================================================
// Work type and handler for the orchestrator dispatch path (mode 1).
// =============================================================================

/// <summary>
/// A minimal work item enqueued by the orchestrator dispatcher.
/// The type is a simple record — no reflection, no codegen.
/// </summary>
/// <param name="Source">Tag identifying the job that enqueued this item.</param>
internal sealed record SmokeWork(string Source);

/// <summary>
/// A no-op work handler for <see cref="SmokeWork"/>. Registered in DI as
/// <c>IWorkHandler&lt;SmokeWork&gt;</c> so the <c>IWorkOrchestrator&lt;SmokeWork&gt;</c>
/// can be constructed by the Bifrost builder. AOT-safe: no reflection, no
/// <c>Activator.CreateInstance</c> — resolved by generic DI at startup.
/// </summary>
internal sealed class SmokeWorkHandler : IWorkHandler<SmokeWork>
{
    /// <inheritdoc/>
    public ValueTask HandleAsync(SmokeWork work, CancellationToken ct)
    {
        Console.WriteLine($"[orchestrator] {work.Source} handled");
        SmokeSignals.OrchestratorHandled.TrySetResult();
        return ValueTask.CompletedTask;
    }
}

// =============================================================================
// Fire signals — AOT-safe TaskCompletionSource holders set inside the handler
// and the inline delegate, awaited by the top-level program with a bounded
// timeout. No reflection, no codegen — plain shared static state.
// =============================================================================

/// <summary>
/// Holds the dispatch-execution signals the smoke test awaits to prove a real
/// fire happened under NativeAOT. Each source is completed exactly once by the
/// first fire of its job; <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>
/// keeps the awaiter off the scheduler tick/pool thread that sets the result.
/// </summary>
internal static class SmokeSignals
{
    /// <summary>
    /// Completed when the orchestrator-dispatched job's handler (mode 1) runs.
    /// </summary>
    public static readonly TaskCompletionSource OrchestratorHandled =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completed when the inline-dispatched job's delegate (mode 2) runs.
    /// </summary>
    public static readonly TaskCompletionSource InlineFired =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// =============================================================================
// Custom dispatcher — resolved via generic DI (AOT-safe, no reflection).
// =============================================================================

/// <summary>
/// A no-op dispatcher used to exercise the custom (<c>DispatchVia&lt;T&gt;</c>)
/// dispatch mode. Registered directly in DI; resolved via
/// <c>GetRequiredService&lt;PrintingDispatcher&gt;()</c> at config time — no
/// type-name lookup, no <c>Activator.CreateInstance</c>, no reflection.
/// Carries no reflective activation patterns, type-name serialization, or
/// expression-tree compilation.
/// </summary>
internal sealed class PrintingDispatcher : IJobDispatcher
{
    /// <inheritdoc/>
    public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
    {
        Console.WriteLine($"[custom] {context.JobName} dispatched at {context.FireTime:u}");
        return ValueTask.CompletedTask;
    }
}
