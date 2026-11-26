// =============================================================================
// Bifrost.AgentFramework.Sample — session-keyed background agent turns.
//
// The app saves each turn request, then queues only the turn ID with Bifrost.
// Keyed dispatch groups turns by (tenant, session): a session runs one turn at a
// time in admission order, and other sessions use the free workers. A scoped
// handler loads the request and the agent session, awaits AIAgent.RunAsync with
// the handler token, and saves the session update and the reply before it
// returns. WithHandlerResilience adds a per-attempt timeout and leaves retries
// off, because an agent turn can call tools with side effects.
//
// The sample uses a local fake IChatClient, so it runs offline. It exits with 1
// if a session ran turns out of order or two turns of one session overlapped.
// See README.md in this folder.
// =============================================================================

using Bifrost.AgentFramework.Sample;
using Bifrost.Core;
using Bifrost.DependencyInjection;
using Bifrost.Resilience;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddScoped<SessionRepository>();
builder.Services.AddSingleton<IChatClient>(new ScriptedChatClient(TimeSpan.FromMilliseconds(50)));
builder.Services.AddSingleton<AIAgent>(static sp => sp.GetRequiredService<IChatClient>()
    .AsAIAgent(instructions: "You are a support assistant. Answer briefly.", name: "support-agent"));

builder.Services.AddWorkOrchestrator<AgentTurn>(static options =>
    {
        options.Capacity = 64;
        options.WorkerCount = 4;
    })
    .WithKeyedDispatch(
        static turn => (turn.TenantId, turn.SessionId),
        maxPendingPerKey: 8,
        maxConcurrencyPerKey: 1)
    .WithScopedHandler(TurnHandler.HandleAsync)
    .WithHandlerResilience(static options =>
    {
        options.AttemptTimeout = TimeSpan.FromSeconds(30);
        options.MaxRetries = 0;
    })
    .Build();

using var host = builder.Build();
await host.StartAsync();

var store = host.Services.GetRequiredService<SessionStore>();
var orchestrator = host.Services.GetRequiredService<IWorkOrchestrator<AgentTurn>>();

var alice = new SessionKey("contoso", "alice");
var bob = new SessionKey("contoso", "bob");
var carol = new SessionKey("fabrikam", "carol");

List<(SessionKey Session, string Message)> submissions = [];
for (var i = 1; i <= 3; i++)
{
    submissions.Add((alice, $"Alice question {i}"));
    submissions.Add((bob, $"Bob question {i}"));
}

for (var i = 1; i <= 12; i++)
{
    submissions.Add((carol, $"Carol burst message {i}"));
}

foreach (var (session, message) in submissions)
{
    var turn = store.SaveRequest(session, message);
    var result = await orchestrator.EnqueueAsync(turn);
    if (result.IsAccepted)
    {
        Console.WriteLine($"accepted  {session,-16} {message}");
    }
    else
    {
        store.MarkRejected(turn.TurnId, result.Reason!.Value);
        Console.WriteLine($"rejected  {session,-16} {message} ({result.Reason})");
    }
}

await orchestrator.DrainAsync();
await host.StopAsync();

return SampleReport.Print(store) ? 0 : 1;
