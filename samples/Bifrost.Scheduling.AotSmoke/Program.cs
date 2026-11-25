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
//
// The smoke runs these steps:
//
//   1. Build the host with all four dispatch modes exercised at DI-config
//      time.
//      - Mode 1 (orchestrator): the standard Bifrost DI builder registers
//        IWorkOrchestrator<SmokeWork>. This is the most generics-heavy,
//        highest-AOT-risk dispatcher. DispatchTo<IWorkOrchestrator<TWork>>
//        resolves the orchestrator at config time through a fully closed
//        generic GetRequiredService<IWorkOrchestrator<SmokeWork>>() call, so
//        OrchestratorJobDispatcher<T> + generic DI run without any runtime
//        type lookup or Activator.CreateInstance. The job fires every ~300 ms,
//        so it actually executes during this run (the tick loop is live after
//        StartAsync). This proves that the orchestrator dispatch path runs
//        end-to-end under NativeAOT, not only that it registered.
//      - Mode 2 (inline): a delegate runs on the scheduler pool thread, with
//        no orchestrator and no reflective activation. It also fires every
//        ~300 ms, so the smoke can assert that the inline fire path executes
//        under NativeAOT.
//      - Mode 4 (custom dispatcher): PrintingDispatcher is registered in DI
//        and resolved at config time through DispatchVia<PrintingDispatcher>()
//        → GetRequiredService<PrintingDispatcher>(). It is resolved by generic
//        DI, not by type name, with no Activator.CreateInstance. This is the
//        key AOT-safety proof.
//
//   2. Register a job at runtime (after DI is built), mode 3 (in-memory
//      store). Starting the host starts SchedulerJobRegistrationService and
//      ScheduleTickLoop. The runtime job goes through IScheduleRegistry with a
//      separate PrintingDispatcher instance for isolation, and the in-memory
//      store persists it.
//
//   3. Inspect the registry. This proves that IBifrostScheduleInspector
//      resolves cleanly.
//
//   4. Verify that all four dispatch modes registered successfully.
//
//   5. Prove that an actual dispatch EXECUTES under NativeAOT (not only
//      registers). The orchestrator and inline jobs fire every ~300 ms, and
//      the tick loop is running, so both must fire. The smoke awaits both
//      signals with a bounded real-wall-clock timeout. Task.WaitAsync bounds
//      the await on the externally completed signal task and throws
//      TimeoutException if the job never fires. It is AOT-safe (no
//      reflection) and the analyzer-clean idiom to bound a wait on a Task
//      that completes outside the flow of this method. If either job does not
//      fire, the fire path is broken under AOT/trim even though registration
//      resolved. The smoke then stops the host, so the process terminates
//      deterministically, and FAILs non-zero.
//
//   6. Exercise the retirement overload through the DI decorator chain under
//      NativeAOT: scale up by one worker, scale down by one, and wait until
//      no worker is active.
// =============================================================================

using Bifrost.Autoscaling;
using Bifrost.Core;
using Bifrost.DependencyInjection;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Console.WriteLine("Bifrost.Scheduling AOT smoke — starting.");

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Warning);
    })
    .ConfigureServices(services =>
    {
        services.AddWorkOrchestrator<SmokeWork>()
                .WithHandler<SmokeWork, SmokeWorkHandler>()
                .UsePriorityDispatch(DispatchStrategy.PriorityExact)
                .WithAutoscaling()
                .Build();

        services.AddSingleton<PrintingDispatcher>();

        services.AddScheduler(b =>
        {
            b.AddJob<SmokeWork>("smoke-orchestrator")
             .Every(TimeSpan.FromMilliseconds(300))
             .DispatchTo<IWorkOrchestrator<SmokeWork>>(
                 static _ => new SmokeWork("smoke-orchestrator"));

            b.AddInlineJob("smoke-inline")
             .Every(TimeSpan.FromMilliseconds(300))
             .Run(static (ctx, ct) =>
             {
                 Console.WriteLine($"[inline] {ctx.JobName} fired at {ctx.FireTime:u}");
                 SmokeSignals.InlineFired.TrySetResult();
                 return ValueTask.CompletedTask;
             });

            b.AddJob<object>("smoke-custom")
             .Every(TimeSpan.FromHours(24))
             .DispatchVia<PrintingDispatcher>();
        });
    })
    .Build();

await host.StartAsync();

var registry = host.Services.GetRequiredService<IScheduleRegistry>();
var runtimeDispatcher = new PrintingDispatcher();

await registry.RegisterAsync(
    "smoke-runtime",
    Cadence.Interval(TimeSpan.FromHours(24)),
    MissedFirePolicy.SkipMissed,
    runtimeDispatcher,
    CancellationToken.None);

Console.WriteLine("Registered runtime job 'smoke-runtime'.");

var inspector = host.Services.GetRequiredService<IBifrostScheduleInspector>();
var jobs = inspector.GetJobs();

Console.WriteLine($"Registry contains {jobs.Count} job(s):");
foreach (var job in jobs)
{
    Console.WriteLine(
        $"  {job.Name}: state={job.State}, nextFire={job.NextFireAt:u}");
}

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

var fireTimeout = TimeSpan.FromSeconds(5);

var orchestratorFired = true;
try
{
    await SmokeSignals.OrchestratorHandled.Task.WaitAsync(fireTimeout);
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
    await SmokeSignals.InlineFired.Task.WaitAsync(fireTimeout);
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
    await host.StopAsync();
    return 1;
}

Console.WriteLine("Orchestrator and inline dispatch paths both executed under NativeAOT.");
var scaling = host.Services.GetRequiredKeyedService<IAutoscalingCoordinator>(typeof(SmokeWork));
var workers = host.Services.GetRequiredKeyedService<IWorkerRegistry>(typeof(SmokeWork));
using (var retirementDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
{
    await scaling.RequestScaleUpAsync(1, retirementDeadline.Token);
    await scaling.RequestScaleDownAsync(1, retirementDeadline.Token);
    while (workers.ActiveWorkerCount != 0)
    {
        await Task.Delay(1, retirementDeadline.Token);
    }
}

Console.WriteLine("Verified dynamic worker retirement under NativeAOT.");
Console.WriteLine("Bifrost.Scheduling AOT smoke — PASS.");

await host.StopAsync();
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
