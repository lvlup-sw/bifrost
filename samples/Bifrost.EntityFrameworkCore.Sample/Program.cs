// =============================================================================
// Bifrost.EntityFrameworkCore.Sample — AddDbContext with a scoped Bifrost handler.
//
// The app registers AppDbContext with AddDbContext and the handler with
// WithHandler<IndexDocument, EfIndexHandler>(ServiceLifetime.Scoped). Bifrost
// creates a DI scope for each handler attempt, so each attempt resolves its own
// DbContext and awaits its database work inside that scope. Keyed dispatch by
// document ID runs the work for one document one item at a time.
//
// The database is SQLite in-memory (shared cache), kept alive by one open
// connection for the life of the process. The documents are committed before
// any work is queued, and a sweep submits work for every document that is not
// indexed yet. A repeated submission of the same operation ID is skipped by the
// handler. See README.md in this folder for the in-memory admission boundary.
// =============================================================================

using Bifrost.Core;
using Bifrost.DependencyInjection;
using Bifrost.EntityFrameworkCore.Sample;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

const string connectionString = "Data Source=bifrost-ef-sample;Mode=Memory;Cache=Shared";
await using var keepAlive = new SqliteConnection(connectionString);
await keepAlive.OpenAsync();

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddDbContext<AppDbContext>(static options => options.UseSqlite(connectionString));

builder.Services.AddWorkOrchestrator<IndexDocument>(static options =>
    {
        options.Capacity = 32;
        options.WorkerCount = 2;
    })
    .WithKeyedDispatch(static work => work.DocumentId, maxPendingPerKey: 4, maxConcurrencyPerKey: 1)
    .WithHandler<IndexDocument, EfIndexHandler>(ServiceLifetime.Scoped)
    .Build();

using var host = builder.Build();
await host.StartAsync();

await using (var scope = host.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    db.Documents.AddRange(SampleReport.CreateDocuments());
    await db.SaveChangesAsync();
}

var orchestrator = host.Services.GetRequiredService<IWorkOrchestrator<IndexDocument>>();
var accepted = await IndexSubmitter.SubmitUnindexedAsync(host.Services, orchestrator);

var repeated = accepted[0];
var repeatResult = await orchestrator.EnqueueAsync(repeated);
Console.WriteLine($"document {repeated.DocumentId}: same operation submitted again, {(repeatResult.IsAccepted ? "accepted" : $"rejected ({repeatResult.Reason})")}");

await orchestrator.DrainAsync();
var passed = await SampleReport.PrintAsync(host.Services);
await host.StopAsync();
return passed ? 0 : 1;
