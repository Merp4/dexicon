using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class SearchesAreRecorded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "search_log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TokenId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    TokenName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Surface = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Corpora = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Query = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    QueryChars = table.Column<int>(type: "INTEGER", nullable: false),
                    Hits = table.Column<int>(type: "INTEGER", nullable: false),
                    TopScore = table.Column<float>(type: "REAL", nullable: true),
                    TookMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Degraded = table.Column<bool>(type: "INTEGER", nullable: false),
                    Noted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_log", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_search_log_AtUtc",
                table: "search_log",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_search_log_Hits_Id",
                table: "search_log",
                columns: new[] { "Hits", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "search_log");
        }
    }
}
