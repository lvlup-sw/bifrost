using Microsoft.EntityFrameworkCore;

namespace Bifrost.EntityFrameworkCore.Sample;

/// <summary>A document the app stores and indexes in the background.</summary>
internal sealed class Document
{
    /// <summary>Gets or sets the document ID.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the tenant that owns the document.</summary>
    public required string TenantId { get; set; }

    /// <summary>Gets or sets the document title.</summary>
    public required string Title { get; set; }

    /// <summary>Gets or sets the document body.</summary>
    public required string Body { get; set; }

    /// <summary>Gets or sets the indexed word count, or <see langword="null"/> before indexing.</summary>
    public int? WordCount { get; set; }

    /// <summary>Gets or sets how many times the index work changed this document.</summary>
    public int IndexRuns { get; set; }
}

/// <summary>
/// Records an index operation that committed. The app uses it to skip an operation it already
/// applied, for example when the same operation is submitted twice.
/// </summary>
internal sealed class IndexOperation
{
    /// <summary>Gets or sets the operation ID.</summary>
    public Guid OperationId { get; set; }

    /// <summary>Gets or sets the document the operation indexed.</summary>
    public int DocumentId { get; set; }
}

/// <summary>The app's EF Core context, registered with <c>AddDbContext</c> (scoped lifetime).</summary>
/// <param name="options">The context options.</param>
internal sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>Gets the documents.</summary>
    public DbSet<Document> Documents => Set<Document>();

    /// <summary>Gets the committed index operations.</summary>
    public DbSet<IndexOperation> IndexOperations => Set<IndexOperation>();

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IndexOperation>().HasKey(static o => o.OperationId);
    }
}
