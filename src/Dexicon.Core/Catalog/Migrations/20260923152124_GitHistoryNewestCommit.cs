using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class GitHistoryNewestCommit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NewestCommitSha",
                table: "sources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NewestCommitUtc",
                table: "sources",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NewestCommitSha",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "NewestCommitUtc",
                table: "sources");
        }
    }
}
