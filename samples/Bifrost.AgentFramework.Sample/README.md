# Agent Framework sample: session-keyed background turns

This sample runs background agent turns with [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) and Bifrost keyed dispatch. It runs offline: a local fake `IChatClient` stands in for the model.

```bash
dotnet run -c Release --project samples/Bifrost.AgentFramework.Sample
```

## What it shows

```csharp
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
```

- **The queue carries a turn ID.** The app saves the turn request first, then calls `EnqueueAsync` with an `AgentTurn(TenantId, SessionId, TurnId)`. The sample prints the `EnqueueResult` for each turn. A rejected turn is marked as rejected in the app's store.
- **One turn per session, in order.** The key is `(TenantId, SessionId)`. With `maxConcurrencyPerKey: 1`, Bifrost starts the next turn of a session only after the previous turn's handler and its scope have exited. Other sessions use the free workers in the meantime.
- **Per-session overload.** A session can have at most 8 queued turns. The running turn does not count. The sample sends a burst of 12 turns to one session, and the extra turns are rejected with `RejectionReason.KeyLimitExceeded`. The exact split depends on how fast the first turn starts. When 64 turns are queued across all sessions, admission rejects with `CapacityExceeded` instead.
- **A scoped handler.** `TurnHandler.HandleAsync` runs in a new DI scope for each attempt. It loads the request and the saved agent session, awaits `AIAgent.RunAsync` with the handler's cancellation token, and saves the session update and the reply before it returns.
- **Timeout without retries.** `WithHandlerResilience` cancels the handler token after 30 seconds. Retries stay off, because an agent turn can call tools with side effects, and a retry could repeat them.

The fake chat client starts each reply with the number of user messages it received. The sample checks that the n-th completed turn of each session saw n user messages, that turns finished in submission order, and that more than one session ran at the same time. It exits with code 1 if a check fails.

## Where recovery ownership begins

`EnqueueResult.Accepted` means the turn entered an in-memory queue in this process. It does not mean the turn ran. If the process stops, queued and running turns are lost. The saved turn requests stay in the app's store with no reply, so the app can find them and submit them again. If a turn must survive a restart without that work, give it to a durable owner, such as the Agent Framework Durable Task extension, and let the durable runtime own retries and completion.

## Dependencies

The Agent Framework package (`Microsoft.Agents.AI`) is referenced only by this sample. Its version is in [`samples/Directory.Packages.props`](../Directory.Packages.props). The Bifrost packages do not depend on it.
