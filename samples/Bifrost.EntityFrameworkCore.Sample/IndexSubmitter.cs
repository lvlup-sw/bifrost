using Bifrost.Core;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.EntityFrameworkCore.Sample;

/// <summary>Finds documents that are not indexed yet and submits index work for them.</summary>
/// <remarks>
/// The database, not the Bifrost queue, records which documents still need work. The app runs
/// this sweep after it saves new documents and again at startup, so documents whose queued work
/// was lost when the process stopped are submitted again.
/// </remarks>
internal static class IndexSubmitter
{
    /// <summary>Submits one index operation for each document that has no word count yet.</summary>
    /// <param name="services">The root service provider.</param>
    /// <param name="orchestrator">The orchestrator.</param>
    /// <param name="ct">Cancels the sweep.</param>
    /// <returns>The accepted operations.</returns>
    public static async Task<IReadOnlyList<IndexDocument>> SubmitUnindexedAsync(
        IServiceProvider services, IWorkWriter<IndexDocument> orchestrator, CancellationToken ct = default)
    {
        List<(int Id, string TenantId)> pending;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            pending = (await db.Documents
                    .Where(static d => d.WordCount == null)
                    .OrderBy(static d => d.Id)
                    .Select(static d => new { d.Id, d.TenantId })
                    .ToListAsync(ct))
                .Select(static d => (d.Id, d.TenantId))
                .ToList();
        }

        var accepted = new List<IndexDocument>();
        foreach (var (id, tenantId) in pending)
        {
            var work = new IndexDocument(Guid.NewGuid(), tenantId, id);
            var result = await orchestrator.EnqueueAsync(work, ct: ct);
            Console.WriteLine($"document {id}: {(result.IsAccepted ? "accepted" : $"rejected ({result.Reason})")}");
            if (result.IsAccepted)
            {
                accepted.Add(work);
            }
        }

        return accepted;
    }
}
