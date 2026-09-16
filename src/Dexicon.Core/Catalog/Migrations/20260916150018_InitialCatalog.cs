using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blobs",
                columns: table => new
                {
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blobs", x => x.Sha256);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Disabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "corpora",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Visibility = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    EmbeddingModel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    EmbeddingDimensions = table.Column<int>(type: "INTEGER", nullable: false),
                    CollectionName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ChunkSize = table.Column<int>(type: "INTEGER", nullable: false),
                    ChunkOverlap = table.Column<int>(type: "INTEGER", nullable: false),
                    BoundaryMode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastIndexedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_corpora", x => x.Id);
                    table.ForeignKey(
                        name: "FK_corpora_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tokens",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TokenHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    TokenSalt = table.Column<byte[]>(type: "BLOB", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false),
                    Scopes = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tokens_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "corpus_grants",
                columns: table => new
                {
                    CorpusId = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_corpus_grants", x => new { x.CorpusId, x.TenantId });
                    table.ForeignKey(
                        name: "FK_corpus_grants_corpora_CorpusId",
                        column: x => x.CorpusId,
                        principalTable: "corpora",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_corpus_grants_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CorpusId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Phase = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    FilesTotal = table.Column<int>(type: "INTEGER", nullable: false),
                    FilesDone = table.Column<int>(type: "INTEGER", nullable: false),
                    FilesSkipped = table.Column<int>(type: "INTEGER", nullable: false),
                    FilesFailed = table.Column<int>(type: "INTEGER", nullable: false),
                    ChunksWritten = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_jobs_corpora_CorpusId",
                        column: x => x.CorpusId,
                        principalTable: "corpora",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sources",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CorpusId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RootPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    IncludeGlobs = table.Column<string>(type: "TEXT", nullable: true),
                    ExcludeGlobs = table.Column<string>(type: "TEXT", nullable: true),
                    UseGitignore = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxFileBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sources_corpora_CorpusId",
                        column: x => x.CorpusId,
                        principalTable: "corpora",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "files",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Language = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ChunkCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ExtractedChars = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    StatusDetail = table.Column<string>(type: "TEXT", nullable: true),
                    IndexedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_files_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_corpora_TenantId_Name",
                table: "corpora",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_corpus_grants_TenantId",
                table: "corpus_grants",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_files_SourceId_RelativePath",
                table: "files",
                columns: new[] { "SourceId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_files_SourceId_Status",
                table: "files",
                columns: new[] { "SourceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_CorpusId_StartedUtc",
                table: "jobs",
                columns: new[] { "CorpusId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_sources_CorpusId",
                table: "sources",
                column: "CorpusId");

            migrationBuilder.CreateIndex(
                name: "IX_tokens_TenantId",
                table: "tokens",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blobs");

            migrationBuilder.DropTable(
                name: "corpus_grants");

            migrationBuilder.DropTable(
                name: "files");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "tokens");

            migrationBuilder.DropTable(
                name: "sources");

            migrationBuilder.DropTable(
                name: "corpora");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
