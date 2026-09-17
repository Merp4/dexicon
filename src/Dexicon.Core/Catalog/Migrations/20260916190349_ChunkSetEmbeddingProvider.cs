using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class ChunkSetEmbeddingProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmbeddingProvider",
                table: "chunk_sets",
                type: "TEXT",
                maxLength: 60,
                nullable: false,
                // Not "": an empty provider resolves to nothing, and every existing set
                // would fail at its next index with "no embedding provider named ''".
                defaultValue: "ollama");

            // Collection names now carry the provider, because two providers can serve a
            // model of the same name and those are NOT the same vectors. Existing rows
            // still hold the old format, so they are rewritten to match what
            // CollectionNameFor now produces; otherwise a set would keep writing into a
            // collection whose name nothing derives any more.
            migrationBuilder.Sql(
                """
                UPDATE chunk_sets
                   SET CollectionName = 'dexicon__ollama__' ||
                       SUBSTR(CollectionName, LENGTH('dexicon__') + 1)
                 WHERE CollectionName LIKE 'dexicon%'
                   AND CollectionName NOT LIKE 'dexicon__ollama__%'
                """);

            // The vectors are in the OLD collections under the old names, so nothing these
            // sets can address holds anything. Pending is the truthful state; the next
            // refresh rebuilds into the renamed collection.
            //
            // The old collections are left in Qdrant rather than dropped from a SQL
            // migration that cannot reach it. They are inert, since no chunk set names them,
            // and safe to delete by hand once the reindex has finished.
            migrationBuilder.Sql(
                "UPDATE file_chunk_states SET ContentHash = NULL, Status = 'Pending', ChunkCount = 0");
            migrationBuilder.Sql("UPDATE chunk_sets SET LastIndexedUtc = NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmbeddingProvider",
                table: "chunk_sets");
        }
    }
}
