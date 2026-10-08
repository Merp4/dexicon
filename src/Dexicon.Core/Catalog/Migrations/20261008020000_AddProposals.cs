using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <summary>
    /// The table proposals are kept in: removals an agent has asked for, and how each was decided.
    /// Empty to begin with, and nothing writes to it until a key holding the propose scope asks
    /// for something. No foreign keys: a proposal outlives the key, the corpus and the thing it
    /// names.
    /// </summary>
    public partial class AddProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "proposals",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TokenId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    TokenName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CorpusId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CorpusName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    TargetLabel = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DecidedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_proposals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_proposals_Kind_TargetId_Status",
                table: "proposals",
                columns: new[] { "Kind", "TargetId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_proposals_Status_CreatedUtc",
                table: "proposals",
                columns: new[] { "Status", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_proposals_TokenId_Status",
                table: "proposals",
                columns: new[] { "TokenId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "proposals");
        }
    }
}
