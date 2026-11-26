using Bifrost.Core;

using Microsoft.EntityFrameworkCore;

namespace Bifrost.EntityFrameworkCore.Sample;

/// <summary>Asks the app to index one stored document.</summary>
/// <param name="OperationId">A stable ID for this operation, used to skip a repeated submission.</param>
/// <param name="TenantId">The tenant that owns the document.</param>
/// <param name="DocumentId">The document to index.</param>
internal sealed record IndexDocument(Guid OperationId, string TenantId, int DocumentId);

/// <summary>
/// A scoped Bifrost handler. Bifrost creates a DI scope for each handler attempt, so each attempt
/// gets its own <see cref="AppDbContext"/>, and disposes the scope after the attempt.
/// </summary>
/// <param name="db">The context of the attempt's scope.</param>
internal sealed class EfIndexHandler(AppDbContext db) : IWorkHandler<IndexDocument>
{
    /// <summary>
    /// Counts the document's words and commits the result together with the operation ID, in one
    /// <c>SaveChangesAsync</c> call.
    /// </summary>
    /// <param name="work">The work item.</param>
    /// <param name="ct">The handler token, passed to every database call.</param>
    /// <returns>A task that completes when the change is committed.</returns>
    /// <remarks>
    /// An operation ID that already committed is skipped, so a repeated submission of the same
    /// operation leaves one effect.
    /// </remarks>
    public async ValueTask HandleAsync(IndexDocument work, CancellationToken ct)
    {
        if (await db.IndexOperations.AnyAsync(o => o.OperationId == work.OperationId, ct))
        {
            return;
        }

        var document = await db.Documents.SingleAsync(d => d.Id == work.DocumentId, ct);
        document.WordCount = document.Body.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        document.IndexRuns++;
        db.IndexOperations.Add(new IndexOperation { OperationId = work.OperationId, DocumentId = work.DocumentId });
        await db.SaveChangesAsync(ct);
    }
}
