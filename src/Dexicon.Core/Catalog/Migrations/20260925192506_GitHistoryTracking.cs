using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class GitHistoryTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GitTracking",
                table: "sources",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GitTracking",
                table: "sources");
        }
    }
}
