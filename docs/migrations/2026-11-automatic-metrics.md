# Automatic orchestrator metrics

Bifrost 0.7.0 records processing metrics for you. Before 0.7.0, `WithOpenTelemetry()` created the processing counters and the duration histogram, but each handler had to call `RecordProcessed` and `RecordFailed`. Now the orchestrator records one terminal outcome for each accepted item, and `WithOpenTelemetry()` turns those outcomes into metrics.

## What to change

1. Keep `WithOpenTelemetry()` on the builder. The meter name is still `Bifrost.{TWorkTypeName}`.
2. Remove every call to `OrchestratorMetrics<TWork>.RecordProcessed` and `RecordFailed` from your handlers. Both methods are now `[Obsolete]`. If you keep the calls, each item is counted twice.
3. Remove the `Stopwatch` and the `try`/`catch` that existed only for metrics. If the handler injected `OrchestratorMetrics<TWork>` only for these calls, remove that dependency too.
4. Keep calls to `RecordEnqueued` and `RecordDeadLettered` if you use them. These two counters are still manual.

Before:

```csharp
public sealed class TurnHandler(OrchestratorMetrics<Turn> metrics) : IWorkHandler<Turn>
{
    public async ValueTask HandleAsync(Turn turn, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await RunTurnAsync(turn, ct);
            metrics.RecordProcessed(sw.Elapsed.TotalMilliseconds);
        }
        catch
        {
            metrics.RecordFailed(sw.Elapsed.TotalMilliseconds);
            throw;
        }
    }
}
```

After:

```csharp
public sealed class TurnHandler : IWorkHandler<Turn>
{
    public ValueTask HandleAsync(Turn turn, CancellationToken ct) => RunTurnAsync(turn, ct);
}
```

## What the numbers mean now

The automatic metrics count items, not handler calls. This changes some values even after you remove the manual calls.

| Instrument | Before (manual) | Now (automatic) |
|---|---|---|
| `orchestrator.items.processed` | One per successful handler call | One per item whose last attempt succeeded |
| `orchestrator.items.failed` | One per failed handler call, so each dead-letter retry counted | One per item whose last attempt failed |
| `orchestrator.processing.duration` | One handler call, no tags | All attempts of one item, tagged by `outcome` |

An item that fails twice and then succeeds on a dead-letter retry is now one processed item and three handler attempts. A canceled item (the handler token was cancelled, for example by `AbortAsync`) is neither processed nor failed. It records a duration with `outcome=Canceled`.

## New instruments

All of these are recorded automatically when you call `WithOpenTelemetry()`:

| Instrument | Type | Tags | Meaning |
|---|---|---|---|
| `bifrost.orchestrator.outcomes` | Counter | `outcome` | One per accepted item: `Succeeded`, `Failed`, `Canceled`, or `Abandoned` |
| `bifrost.orchestrator.handler.attempts` | Counter | none | Every handler call, including dead-letter retries |
| `bifrost.orchestrator.deadletter.captured` | Counter | none | Final handler failures stored in the dead-letter queue |
| `bifrost.orchestrator.deadletter.capture_errors` | Counter | none | Final handler failures the dead-letter queue failed to store |
| `bifrost.orchestrator.deadletter.evictions` | Counter | none | Entries the built-in dead-letter queue dropped because it was full |
| `bifrost.orchestrator.deadletter.notifications_dropped` | Counter | none | Dead-letter subscriber notifications that were dropped |
| `bifrost.orchestrator.queue.ready` | Gauge | none | Pending items that a free worker could take now |
| `bifrost.orchestrator.queue.blocked` | Gauge | none | Pending items that wait for a key slot (keyed dispatch only) |

`orchestrator.queue.pending` is still the total pending count: ready plus blocked. Without keyed dispatch, every pending item is ready and blocked is always zero.

`Abandoned` means the item was accepted but abort or completion removed it from the queue before any worker started it. An abandoned item records no duration and no attempts.

`bifrost.orchestrator.rejected` is unchanged and still counts admission rejections by `work.class` and `rejection.reason`. With keyed dispatch it also reports `rejection.reason=KeyLimitExceeded`.

## Labels and keyed dispatch

Every tag value comes from a fixed list of enum names: `work.class`, `rejection.reason`, and `outcome`. Work items and work keys never become tags. The ready and blocked gauges are totals across all keys. The number of metric series does not grow with the number of keys, so you can use session or tenant keys with `WithKeyedDispatch` without high-cardinality metrics.

If you need per-key numbers, record them yourself with a bounded label (for example a tenant tier, not a tenant ID).

## Builder order

Processing metrics come from the terminal outcome stage, which runs in fixed steps inside the orchestrator: the handler pipeline and its scope exit, the key slot is released, the outcome is recorded (with any dead-letter capture), and then the worker reports idle. The order of `WithOpenTelemetry`, `WithDeadLetterQueue`, `WithEventStream`, and `WithHandlerDecorator` on the builder does not change the outcomes, the capture, or the metrics.
