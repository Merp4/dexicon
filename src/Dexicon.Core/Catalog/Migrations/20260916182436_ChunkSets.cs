using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class ChunkSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChunkSetId",
                table: "jobs",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "chunk_sets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CorpusId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    EmbeddingModel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    EmbeddingDimensions = table.Column<int>(type: "INTEGER", nullable: false),
                    CollectionName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ChunkSize = table.Column<int>(type: "INTEGER", nullable: false),
                    ChunkOverlap = table.Column<int>(type: "INTEGER", nullable: false),
                    BoundaryMode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CustomBoundaryPattern = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    UnitAware = table.Column<bool>(type: "INTEGER", nullable: false),
                    SentenceAware = table.Column<bool>(type: "INTEGER", nullable: false),
                    HeadingContext = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastIndexedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chunk_sets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_chunk_sets_corpora_CorpusId",
                        column: x => x.CorpusId,
                        principalTable: "corpora",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "file_chunk_states",
                columns: table => new
                {
                    FileId = table.Column<string>(type: "TEXT", nullable: false),
                    ChunkSetId = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ChunkCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    StatusDetail = table.Column<string>(type: "TEXT", nullable: true),
                    IndexedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_chunk_states", x => new { x.FileId, x.ChunkSetId });
                    table.ForeignKey(
                        name: "FK_file_chunk_states_chunk_sets_ChunkSetId",
                        column: x => x.ChunkSetId,
                        principalTable: "chunk_sets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_file_chunk_states_files_FileId",
                        column: x => x.FileId,
                        principalTable: "files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });


            // ── Carry the existing configuration into the new shape ──────────────
            //
            // Ordering is the whole point of hand-writing this: EF generated the DROPs
            // first, which would have deleted every corpus's model and chunk settings
            // before there was anywhere to put them. A schema migration that loses the
            // data it was meant to move is not a migration.
            //
            // Each corpus gets one set named "default", carrying exactly what it had, so
            // nothing re-chunks and nothing re-embeds on upgrade. A deterministic id,
            // the corpus id with a prefix, keeps this repeatable and lets the second
            // statement join without a temporary table.
            migrationBuilder.Sql(
                """
                INSERT INTO chunk_sets (
                    Id, CorpusId, Name, Description, EmbeddingModel, EmbeddingDimensions,
                    CollectionName, ChunkSize, ChunkOverlap, BoundaryMode, CustomBoundaryPattern,
                    UnitAware, SentenceAware, HeadingContext, IsDefault, State, CreatedUtc, LastIndexedUtc)
                SELECT
                    'cs_' || c.Id, c.Id, 'default', NULL, c.EmbeddingModel, c.EmbeddingDimensions,
                    c.CollectionName, c.ChunkSize, c.ChunkOverlap, c.BoundaryMode, NULL,
                    -- LastIndexedUtc is deliberately NULL, not carried over: the corpus was
                    -- indexed, but not into anything this set can reach. A timestamp here
                    -- would claim the set has content when every one of its files is Pending.
                    0, 0, 0, 1, c.State, c.CreatedUtc, NULL
                FROM corpora c
                """);

            // Every file gets a state row for its corpus's default set, all Pending.
            //
            // Pending rather than a copy of the old status, because the vectors those
            // statuses described are no longer reachable: a point's id is now derived from
            // the chunk set rather than the corpus, and the new filter requires a
            // chunk_set_id payload that no existing point carries. Claiming "indexed" here
            // would mean claiming search works when nothing matches.
            //
            // Joined through sources: a file knows its source and a source knows its
            // corpus; there has never been a direct link.
            migrationBuilder.Sql(
                """
                INSERT INTO file_chunk_states (
                    FileId, ChunkSetId, ContentHash, ChunkCount, Status, StatusDetail, IndexedUtc)
                SELECT f.Id, 'cs_' || s.CorpusId, NULL, 0, 'Pending', NULL, NULL
                FROM files f
                JOIN sources s ON s.Id = f.SourceId
                """);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_ChunkSetId",
                table: "jobs",
                column: "ChunkSetId");

            migrationBuilder.CreateIndex(
                name: "IX_chunk_sets_CorpusId_IsDefault",
                table: "chunk_sets",
                columns: new[] { "CorpusId", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_chunk_sets_CorpusId_Name",
                table: "chunk_sets",
                columns: new[] { "CorpusId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_chunk_states_ChunkSetId_Status",
                table: "file_chunk_states",
                columns: new[] { "ChunkSetId", "Status" });

            migrationBuilder.AddForeignKey(
                name: "FK_jobs_chunk_sets_ChunkSetId",
                table: "jobs",
                column: "ChunkSetId",
                principalTable: "chunk_sets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Safe now: everything above has been copied into the new tables.
            migrationBuilder.DropIndex(
                name: "IX_files_SourceId_Status",
                table: "files");

            migrationBuilder.DropColumn(
                name: "ChunkCount",
                table: "files");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "files");

            migrationBuilder.DropColumn(
                name: "IndexedUtc",
                table: "files");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "files");

            migrationBuilder.DropColumn(
                name: "StatusDetail",
                table: "files");

            migrationBuilder.DropColumn(
                name: "BoundaryMode",
                table: "corpora");

            migrationBuilder.DropColumn(
                name: "ChunkOverlap",
                table: "corpora");

            migrationBuilder.DropColumn(
                name: "ChunkSize",
                table: "corpora");

            migrationBuilder.DropColumn(
                name: "CollectionName",
                table: "corpora");

            migrationBuilder.DropColumn(
                name: "EmbeddingDimensions",
                table: "corpora");

            migrationBuilder.DropColumn(
                name: "EmbeddingModel",
                table: "corpora");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_jobs_chunk_sets_ChunkSetId",
                table: "jobs");

            migrationBuilder.DropTable(
                name: "file_chunk_states");

            migrationBuilder.DropTable(
                name: "chunk_sets");

            migrationBuilder.DropIndex(
                name: "IX_jobs_ChunkSetId",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "ChunkSetId",
                table: "jobs");

            migrationBuilder.AddColumn<int>(
                name: "ChunkCount",
                table: "files",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "files",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IndexedUtc",
                table: "files",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "files",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StatusDetail",
                table: "files",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BoundaryMode",
                table: "corpora",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ChunkOverlap",
                table: "corpora",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ChunkSize",
                table: "corpora",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CollectionName",
                table: "corpora",
                type: "TEXT",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "EmbeddingDimensions",
                table: "corpora",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingModel",
                table: "corpora",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_files_SourceId_Status",
                table: "files",
                columns: new[] { "SourceId", "Status" });
        }
    }
}
