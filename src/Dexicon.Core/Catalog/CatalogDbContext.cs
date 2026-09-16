using Microsoft.EntityFrameworkCore;

namespace Dexicon.Core.Catalog;

/// <summary>
/// The control plane. Everything that needs listing, filtering, joining, counting or a
/// transaction lives here; Qdrant holds only chunks and vectors and is fully
/// reconstructible from this plus the sources. See docs/03-data-model.md.
/// </summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<ApiToken> Tokens => Set<ApiToken>();
    public DbSet<Corpus> Corpora => Set<Corpus>();
    public DbSet<CorpusGrant> CorpusGrants => Set<CorpusGrant>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<IndexedFile> Files => Set<IndexedFile>();
    public DbSet<Blob> Blobs => Set<Blob>();
    public DbSet<BlobText> BlobTexts => Set<BlobText>();
    public DbSet<IndexJob> Jobs => Set<IndexJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(40);
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<ApiToken>(e =>
        {
            e.ToTable("tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(40);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Scopes).HasMaxLength(200).IsRequired();
            e.HasOne(x => x.Tenant).WithMany(t => t.Tokens)
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<Corpus>(e =>
        {
            e.ToTable("corpora");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(40);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.EmbeddingModel).HasMaxLength(200).IsRequired();
            e.Property(x => x.CollectionName).HasMaxLength(300).IsRequired();
            e.Property(x => x.BoundaryMode).HasMaxLength(40).IsRequired();
            e.Property(x => x.Visibility).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.Tenant).WithMany(t => t.Corpora)
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            // A tenant cannot have two corpora with the same name; the name is what
            // an agent passes to search_index, so it has to resolve unambiguously.
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<CorpusGrant>(e =>
        {
            e.ToTable("corpus_grants");
            e.HasKey(x => new { x.CorpusId, x.TenantId });
            e.HasOne(x => x.Corpus).WithMany(c => c.Grants)
                .HasForeignKey(x => x.CorpusId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Tenant).WithMany()
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Source>(e =>
        {
            e.ToTable("sources");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(40);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RootPath).HasMaxLength(1000);
            e.HasOne(x => x.Corpus).WithMany(c => c.Sources)
                .HasForeignKey(x => x.CorpusId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IndexedFile>(e =>
        {
            e.ToTable("files");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(40);
            e.Property(x => x.RelativePath).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.MediaType).HasMaxLength(200);
            e.Property(x => x.Language).HasMaxLength(40);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.Source).WithMany(s => s.Files)
                .HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.SourceId, x.RelativePath }).IsUnique();
            e.HasIndex(x => new { x.SourceId, x.Status });
            e.Property(x => x.BlobSha256).HasMaxLength(64);
            // Restrict, not Cascade: deleting a blob that corpora still reference would
            // silently empty them. A blob is only removable once nothing attaches it.
            e.HasOne(x => x.Blob).WithMany()
                .HasForeignKey(x => x.BlobSha256).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.BlobSha256);
        });

        modelBuilder.Entity<Blob>(e =>
        {
            e.ToTable("blobs");
            e.HasKey(x => x.Sha256);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.MediaType).HasMaxLength(200);
            e.Property(x => x.OriginalFileName).HasMaxLength(500);
            e.HasOne(x => x.Text).WithOne(t => t.Blob)
                .HasForeignKey<BlobText>(t => t.Sha256).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BlobText>(e =>
        {
            e.ToTable("blob_texts");
            e.HasKey(x => x.Sha256);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.Extractor).HasMaxLength(100).IsRequired();
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.EmptyReason).HasMaxLength(500);
        });

        modelBuilder.Entity<IndexJob>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(40);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Phase).HasMaxLength(40);
            e.HasOne(x => x.Corpus).WithMany(c => c.Jobs)
                .HasForeignKey(x => x.CorpusId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.CorpusId, x.StartedUtc });
        });
    }
}
