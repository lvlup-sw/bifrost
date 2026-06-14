// =============================================================================
// Bifrost.Scheduling.AotSmoke — NativeAOT compatibility smoke test (DR-13)
//
// Exercises three dispatch modes under a NativeAOT-published binary to verify:
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
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Console.WriteLine("Bifrost.Scheduling AOT smoke — starting.");

// ─────────────────────────────────────────────────────────────────────────────
// 1. Build the host with all three dispatch modes exercised at DI-config time.
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
        // Register a custom dispatcher for mode 3 (resolved by generic DI, not
        // by type name — this is the key AOT-safety proof).
        services.AddSingleton<PrintingDispatcher>();

        services.AddScheduler(b =>
        {
            // Mode 2 — inline dispatch: a delegate runs on the scheduler pool thread.
            // No orchestrator, no reflective activation.
            b.AddInlineJob("smoke-inline")
             .Every(TimeSpan.FromHours(24))
             .Run(static (ctx, ct) =>
             {
                 Console.WriteLine($"[inline] {ctx.JobName} fired at {ctx.FireTime:u}");
                 return ValueTask.CompletedTask;
             });

            // Mode 3 — custom dispatcher resolved from DI at config time via
            // DispatchVia<PrintingDispatcher>() → GetRequiredService<PrintingDispatcher>().
            // No type-name lookup, no Activator.CreateInstance.
            b.AddJob<object>("smoke-custom")
             .Every(TimeSpan.FromHours(24))
             .DispatchVia<PrintingDispatcher>();
        });
    })
    .Build();

// ─────────────────────────────────────────────────────────────────────────────
// 2. Register a job at runtime (after DI is built) — mode 1 (in-memory store).
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
// 4. Verify all three dispatch modes registered successfully.
// ─────────────────────────────────────────────────────────────────────────────

var jobNames = jobs.Select(j => j.Name).ToHashSet(StringComparer.Ordinal);
if (!jobNames.Contains("smoke-inline"))
{
    Console.Error.WriteLine("FAIL: 'smoke-inline' (mode 2 — inline dispatch) not registered.");
    return 1;
}

if (!jobNames.Contains("smoke-custom"))
{
    Console.Error.WriteLine("FAIL: 'smoke-custom' (mode 3 — custom dispatcher) not registered.");
    return 1;
}

if (!jobNames.Contains("smoke-runtime"))
{
    Console.Error.WriteLine("FAIL: 'smoke-runtime' (mode 1 — runtime registration) not registered.");
    return 1;
}

Console.WriteLine("All three dispatch modes registered and resolved cleanly.");
Console.WriteLine("Bifrost.Scheduling AOT smoke — PASS.");

await host.StopAsync().ConfigureAwait(false);
return 0;

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
