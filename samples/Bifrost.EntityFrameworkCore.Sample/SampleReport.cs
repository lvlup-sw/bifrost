using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.EntityFrameworkCore.Sample;

/// <summary>Seeds the sample documents and checks the result after the orchestrator drained.</summary>
internal static class SampleReport
{
    /// <summary>Gets the sample documents.</summary>
    /// <returns>New, unsaved documents.</returns>
    public static IEnumerable<Document> CreateDocuments() =>
    [
        new() { TenantId = "contoso", Title = "Release notes", Body = "Keyed dispatch ships in this release" },
        new() { TenantId = "contoso", Title = "Runbook", Body = "Drain the orchestrator before a deploy" },
        new() { TenantId = "fabrikam", Title = "Welcome", Body = "Hello and welcome" },
        new() { TenantId = "fabrikam", Title = "Policy", Body = "Admission is not durability" },
        new() { TenantId = "fabrikam", Title = "FAQ", Body = "Ask the durable owner to keep work across restarts" },
    ];

    /// <summary>Prints the documents and returns whether every check passed.</summary>
    /// <param name="services">The root service provider.</param>
    /// <returns><see langword="true"/> when every document was indexed exactly once.</returns>
    public static async Task<bool> PrintAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var documents = await db.Documents.OrderBy(static d => d.Id).ToListAsync();
        var operations = await db.IndexOperations.CountAsync();

        var failures = new List<string>();
        Console.WriteLine();
        foreach (var document in documents)
        {
            Console.WriteLine($"document {document.Id} {document.TenantId}/{document.Title}: {document.WordCount} words, indexed {document.IndexRuns} time(s)");
            var expected = document.Body.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (document.WordCount != expected || document.IndexRuns != 1)
            {
                failures.Add($"document {document.Id} has {document.WordCount} words after {document.IndexRuns} run(s); expected {expected} after 1.");
            }
        }

        Console.WriteLine($"committed operations: {operations}");
        if (operations != documents.Count)
        {
            failures.Add($"{operations} operations committed for {documents.Count} documents.");
        }

        foreach (var failure in failures)
        {
            await Console.Error.WriteLineAsync($"FAIL: {failure}");
        }

        Console.WriteLine(failures.Count == 0 ? "PASS" : "FAIL");
        return failures.Count == 0;
    }
}
