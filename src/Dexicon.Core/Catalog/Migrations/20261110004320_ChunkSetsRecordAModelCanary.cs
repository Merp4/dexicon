using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <summary>
    /// Give each chunk set room to record what its embedding model answers to a fixed string:
    /// <c>CanaryVector</c> (float32, little-endian), the cosine similarity at the last check
    /// (<c>CanarySimilarity</c>) and when it was taken (<c>CanaryCheckedUtc</c>). See
    /// <c>ModelCanary</c> and D-46.
    ///
    /// All three are null for existing sets, and no statement fills them: the value is the model's
    /// answer, which only the model can give. The first check after the upgrade records it, from the
    /// model as it is then, so a change made before that check is not visible afterwards.
    ///
    /// <c>Down</c> drops the columns, and the next <c>Up</c> records afresh. A version without this
    /// migration ignores the columns and leaves them null in the sets it creates.
    /// </summary>
    public partial class ChunkSetsRecordAModelCanary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CanaryCheckedUtc",
                table: "chunk_sets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CanarySimilarity",
                table: "chunk_sets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "CanaryVector",
                table: "chunk_sets",
                type: "BLOB",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanaryCheckedUtc",
                table: "chunk_sets");

            migrationBuilder.DropColumn(
                name: "CanarySimilarity",
                table: "chunk_sets");

            migrationBuilder.DropColumn(
                name: "CanaryVector",
                table: "chunk_sets");
        }
    }
}
