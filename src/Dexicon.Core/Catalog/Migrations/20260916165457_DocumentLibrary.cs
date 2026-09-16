using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class DocumentLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BlobSha256",
                table: "files",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalFileName",
                table: "blobs",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "blob_texts",
                columns: table => new
                {
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    UnitsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ExtractedChars = table.Column<int>(type: "INTEGER", nullable: false),
                    Extractor = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ExtractedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EmptyReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blob_texts", x => x.Sha256);
                    table.ForeignKey(
                        name: "FK_blob_texts_blobs_Sha256",
                        column: x => x.Sha256,
                        principalTable: "blobs",
                        principalColumn: "Sha256",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_files_BlobSha256",
                table: "files",
                column: "BlobSha256");

            migrationBuilder.AddForeignKey(
                name: "FK_files_blobs_BlobSha256",
                table: "files",
                column: "BlobSha256",
                principalTable: "blobs",
                principalColumn: "Sha256",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_files_blobs_BlobSha256",
                table: "files");

            migrationBuilder.DropTable(
                name: "blob_texts");

            migrationBuilder.DropIndex(
                name: "IX_files_BlobSha256",
                table: "files");

            migrationBuilder.DropColumn(
                name: "BlobSha256",
                table: "files");

            migrationBuilder.DropColumn(
                name: "OriginalFileName",
                table: "blobs");
        }
    }
}
