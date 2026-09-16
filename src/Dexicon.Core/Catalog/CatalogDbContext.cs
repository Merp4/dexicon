using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

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
    public DbSet<ChunkSet> ChunkSets => Set<ChunkSet>();
    public DbSet<EmbeddingModelProfile> ModelProfiles => Set<EmbeddingModelProfile>();
    public DbSet<FileChunkState> FileChunkStates => Set<FileChunkState>();
    public DbSet<CorpusGrant> CorpusGrants => Set<CorpusGrant>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<IndexedFile> Files => Set<IndexedFile>();
    public DbSet<Blob> Blobs => Set<Blob>();
    public DbSet<BlobText> BlobTexts => Set<BlobText>();
    public DbSet<IndexJob> Jobs => Set<IndexJob>();

    /// <summary>
    /// UTC everywhere, converted exactly once, at the edge that renders it.
    ///
    /// SQLite has no date type, so EF stores a DateTime as text and reads it back with
    /// <c>Kind = Unspecified</c>. System.Text.Json then serialises it WITHOUT a `Z`, and
    /// <c>new Date("2026-09-16T17:08:11")</c> in a browser parses that as LOCAL time — so
    /// every timestamp in the UI was silently wrong by the viewer's UTC offset, and job
    /// times did not line up with the log.
    ///
    /// One convention fixes the whole class rather than each read site remembering:
    /// values go in as UTC and come out tagged as UTC. Nothing else in the codebase
    /// converts, and no property needs to opt in.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter()
        : ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter()
        : ValueConverter<DateTime?, DateTime?>(
            v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()) : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

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
            e.Property(x => x.Visibility).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.Tenant).WithMany(t => t.Corpora)
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            // A tenant cannot have two corpora with the same name; the name is what
            // an agent passes to search_index, so it has to resolve unambiguously.
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<EmbeddingModelProfile>(e =>
        {
            e.ToTable("model_profiles");
            // Keyed on both: two providers can serve a model of the same name, and their
            // task framing is not necessarily the same.
            e.HasKey(x => new { x.Provider, x.Model });
            e.Property(x => x.Provider).HasMaxLength(40);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.DocumentTemplate).HasMaxLength(1000).IsRequired();
            e.Property(x => x.QueryTemplate).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(1000);
        });

        modelBuilder.Entity<ChunkSet>(e =>
        {
            e.ToTable("chunk_sets");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(40);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.EmbeddingProvider).HasMaxLength(60).IsRequired();
            e.Property(x => x.EmbeddingModel).HasMaxLength(200).IsRequired();
            e.Property(x => x.CollectionName).HasMaxLength(300).IsRequired();
            e.Property(x => x.BoundaryMode).HasMaxLength(40).IsRequired();
            e.Property(x => x.CustomBoundaryPattern).HasMaxLength(500);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.Corpus).WithMany(c => c.ChunkSets)
                .HasForeignKey(x => x.CorpusId).OnDelete(DeleteBehavior.Cascade);
            // `corpus:set` has to resolve unambiguously, the same way a corpus name does.
            e.HasIndex(x => new { x.CorpusId, x.Name }).IsUnique();
            e.HasIndex(x => new { x.CorpusId, x.IsDefault });
        });

        modelBuilder.Entity<FileChunkState>(e =>
        {
            e.ToTable("file_chunk_states");
            e.HasKey(x => new { x.FileId, x.ChunkSetId });
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.File).WithMany(f => f.ChunkStates)
                .HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ChunkSet).WithMany(s => s.Files)
                .HasForeignKey(x => x.ChunkSetId).OnDelete(DeleteBehavior.Cascade);
            // The indexer's hot read: "what does this set still have to do?"
            e.HasIndex(x => new { x.ChunkSetId, x.Status });
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
            e.Property(x => x.MediaType).HasMaxLength(200);
            e.Property(x => x.Language).HasMaxLength(40);
            e.HasOne(x => x.Source).WithMany(s => s.Files)
                .HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.SourceId, x.RelativePath }).IsUnique();
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
            e.Property(x => x.ChunkSetId).HasMaxLength(40);
            // SetNull, not Cascade: dropping a set after promoting its replacement must
            // not erase the record of the job that built it.
            e.HasOne(x => x.ChunkSet).WithMany()
                .HasForeignKey(x => x.ChunkSetId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Phase).HasMaxLength(40);
            e.HasOne(x => x.Corpus).WithMany(c => c.Jobs)
                .HasForeignKey(x => x.CorpusId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.CorpusId, x.StartedUtc });
        });
    }
}
