# EF Core sample: a scoped Bifrost handler

This sample registers an EF Core `DbContext` with `AddDbContext` and runs a scoped Bifrost handler that uses it. The database is SQLite in-memory, so the sample runs without a database server.

```bash
dotnet run -c Release --project samples/Bifrost.EntityFrameworkCore.Sample
```

## What it shows

```csharp
builder.Services.AddDbContext<AppDbContext>(static options => options.UseSqlite(connectionString));

builder.Services.AddWorkOrchestrator<IndexDocument>(static options =>
    {
        options.Capacity = 32;
        options.WorkerCount = 2;
    })
    .WithKeyedDispatch(static work => work.DocumentId, maxPendingPerKey: 4, maxConcurrencyPerKey: 1)
    .WithHandler<IndexDocument, EfIndexHandler>(ServiceLifetime.Scoped)
    .Build();
```

- **A scope for each attempt.** With `ServiceLifetime.Scoped`, Bifrost creates a DI scope for each handler attempt and disposes it after the attempt. `EfIndexHandler` takes `AppDbContext` in its constructor, so each attempt gets its own context. The handler awaits its database work inside that scope and passes the handler token to every call. `WithScopedHandler((services, work, ct) => ...)` does the same for a delegate.
- **One item per document at a time.** Keyed dispatch by document ID runs the work for one document one item at a time, so two attempts never update the same row at once. Work for other documents runs on the other worker.
- **The database owns the state.** The app commits the documents first and then submits work. `IndexSubmitter.SubmitUnindexedAsync` finds every document that has no word count yet and submits one `IndexDocument` for each. The handler commits the word count and the operation ID in one `SaveChangesAsync` call, and it skips an operation ID that already committed. The sample submits one operation twice to show that the second run changes nothing.

SQLite in-memory databases live only while a connection is open. The sample keeps one connection open for the life of the process and uses a shared cache, so each `DbContext` sees the same database.

## The in-memory admission boundary

`EnqueueAsync` returns `EnqueueResult.Accepted` when the item enters Bifrost's queue. That queue is in memory in this process. Acceptance does not mean the handler ran or that the change committed:

- If the process stops, queued items and running attempts are lost. Bifrost does not save them and does not run them again after a restart.
- A rejected item (for example `CapacityExceeded` or `KeyLimitExceeded`) never entered the queue. The caller decides whether to try again later.
- Only the handler's own `SaveChangesAsync` makes the effect durable.

So the recovery owner is the app's database, not Bifrost. In this sample, a document with no word count still needs work. The app runs the same sweep after it saves documents and again at startup, and it submits work for anything that the last process accepted but did not finish. The operation ID makes a repeated run safe.

When work must survive a restart without such a sweep, submit it to a durable owner, for example a Wolverine durable queue fed by an EF Core transactional outbox, and let that owner hold retries and completion. Bifrost then runs the local attempt.

## Dependencies

The EF Core package (`Microsoft.EntityFrameworkCore.Sqlite`) is referenced only by this sample. Its version is in [`samples/Directory.Packages.props`](../Directory.Packages.props).
