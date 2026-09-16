using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class BlobTextExtractorVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExtractorVersion",
                table: "blob_texts",
                type: "INTEGER",
                nullable: false,
                // 0 deliberately: every row already in the catalogue predates versioning,
                // so all of it re-extracts on next use. That is the point of the column.
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExtractorVersion",
                table: "blob_texts");
        }
    }
}
