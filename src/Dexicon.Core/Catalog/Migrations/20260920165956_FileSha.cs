using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class FileSha : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Sha256",
                table: "files",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Sha256",
                table: "files");
        }
    }
}
