// =============================================================================
// Bifrost.Keyed.AotSmoke — NativeAOT compatibility smoke test for keyed dispatch.
//
// Publishes the 0.7.0 keyed path under NativeAOT and checks that it runs:
//   • WithKeyedDispatch (KeyedWorkQueue<TWork, TKey> with a string key),
//   • WithScopedHandler (a fresh DI scope per attempt),
//   • WithHandlerResilience (Polly v8 cooperative timeout, no retries),
// all resolved through the generic DI builder with no reflection-based
// activation. A publish with IL2xxx or IL3050 warnings fails the build because
// warnings are errors.
//
// The smoke runs these steps with two workers, Capacity 16, and at most 3
// pending items per key:
//
//   1. Start A1, whose handler waits on a gate. Key A is now at its
//      concurrency limit of 1.
//   2. Queue A2..A4 (accepted: 3 pending) and A5 (KeyLimitExceeded).
//   3. Run B1 to completion while A1 still holds its slot: a saturated key
//      leaves the free worker to another key.
//   4. Start B2, which also waits on the gate, so both workers are busy.
//   5. Queue C0..C12 on distinct keys until 16 items are pending. Then A6
//      and D0 are rejected with CapacityExceeded: the global limit wins over
//      the per-key limit.
//   6. Open the gate, drain, and check that key A ran A1..A4 in admission
//      order, every accepted item ran once in its own scope, every scope was
//      disposed, and nothing is left pending.
//
// The program exits with 0 on success and 1 on the first failed check.
// =============================================================================

using Bifrost.Core;
using Bifrost.DependencyInjection;
using Bifrost.Keyed.AotSmoke;
using Bifrost.Resilience;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Console.WriteLine("Bifrost.Keyed AOT smoke — starting.");

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddSingleton<SmokeState>();
builder.Services.AddScoped<ScopeProbe>();
builder.Services.AddWorkOrchestrator<SmokeTurn>(static options =>
    {
        options.Capacity = 16;
        options.WorkerCount = 2;
    })
    .WithKeyedDispatch(static turn => turn.Key, maxPendingPerKey: 3, maxConcurrencyPerKey: 1)
    .WithScopedHandler(SmokeHandler.HandleAsync)
    .WithHandlerResilience(static options => options.AttemptTimeout = TimeSpan.FromSeconds(30))
    .Build();

using var host = builder.Build();
await host.StartAsync();

var state = host.Services.GetRequiredService<SmokeState>();
var orchestrator = host.Services.GetRequiredService<IWorkOrchestrator<SmokeTurn>>();
var timeout = TimeSpan.FromSeconds(10);
var checks = new SmokeChecks();

checks.Expect(orchestrator.Enqueue(new SmokeTurn("A", 1, Block: true)).IsAccepted, "A1 accepted");
await state.Started("A1").WaitAsync(timeout);

for (var i = 2; i <= 4; i++)
{
    checks.Expect(orchestrator.Enqueue(new SmokeTurn("A", i)).IsAccepted, $"A{i} accepted while A1 runs");
}

checks.ExpectReason(orchestrator.Enqueue(new SmokeTurn("A", 5)), RejectionReason.KeyLimitExceeded, "A5");

checks.Expect(orchestrator.Enqueue(new SmokeTurn("B", 1)).IsAccepted, "B1 accepted");
await state.Completed("B1").WaitAsync(timeout);
checks.Expect(!state.Completed("A1").IsCompleted, "B1 finished on the free worker while A1 still runs");

checks.Expect(orchestrator.Enqueue(new SmokeTurn("B", 2, Block: true)).IsAccepted, "B2 accepted");
await state.Started("B2").WaitAsync(timeout);

for (var i = 0; i <= 12; i++)
{
    checks.Expect(orchestrator.Enqueue(new SmokeTurn($"C{i}", 0)).IsAccepted, $"C{i} accepted");
}

checks.Expect(orchestrator.PendingCount == 16, $"16 items pending (was {orchestrator.PendingCount})");
checks.ExpectReason(orchestrator.Enqueue(new SmokeTurn("A", 6)), RejectionReason.CapacityExceeded, "A6");
checks.ExpectReason(orchestrator.Enqueue(new SmokeTurn("D0", 0)), RejectionReason.CapacityExceeded, "D0");

state.OpenGate();
await orchestrator.DrainAsync().WaitAsync(timeout);
await host.StopAsync();

checks.Expect(state.Order("A").SequenceEqual([1, 2, 3, 4]), $"key A ran in admission order (was {string.Join(",", state.Order("A"))})");
checks.Expect(state.TotalCompleted == 19, $"19 accepted items ran (was {state.TotalCompleted})");
checks.Expect(state.ScopesCreated == 19 && state.ScopesDisposed == 19, $"one disposed scope per attempt (created {state.ScopesCreated}, disposed {state.ScopesDisposed})");
checks.Expect(orchestrator.PendingCount == 0, "nothing pending after drain");

return checks.Finish();
