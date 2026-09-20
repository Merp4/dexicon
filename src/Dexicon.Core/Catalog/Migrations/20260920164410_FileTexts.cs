using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class FileTexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "file_texts",
                columns: table => new
                {
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    UnitsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ExtractedChars = table.Column<int>(type: "INTEGER", nullable: false),
                    Extractor = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ExtractorVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ExtractedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EmptyReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_texts", x => x.Sha256);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "file_texts");
        }
    }
}
