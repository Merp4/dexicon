using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AdminPasswordAndScopedKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Outside the transaction, which is the whole point. EF emits its own
            // `PRAGMA foreign_keys = 0` around a SQLite table rebuild, but SQLite ignores
            // that pragma inside a transaction and EF warns that it does. Dropping the
            // TenantId columns rebuilds corpora and tokens, EF defers those rebuilds to the
            // end of the migration, and the tenants table is therefore dropped while rows
            // still reference it. Suppressing the transaction makes the pragma take effect.
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);

            // FIRST, while the schema is still the old one and no table rebuild is pending.
            //
            // Names become unique across the install where they were unique per tenant, so
            // two tenants may each hold a corpus called "docs" and the index built at the end
            // would refuse. Renaming the later ones is the only option that keeps both:
            // failing strands the catalogue, and dropping one loses an index. The oldest
            // keeps the bare name; the others gain a short suffix an operator can change in
            // the UI.
            migrationBuilder.Sql(@"
                UPDATE corpora
                   SET Name = Name || '-' || substr(Id, 1, 6)
                 WHERE Id IN (
                       SELECT Id FROM (
                              SELECT Id,
                                     ROW_NUMBER() OVER (
                                         PARTITION BY Name ORDER BY CreatedUtc, Id) AS rn
                                FROM corpora)
                        WHERE rn > 1);");

            migrationBuilder.DropForeignKey(
                name: "FK_corpora_tenants_TenantId",
                table: "corpora");

            migrationBuilder.DropForeignKey(
                name: "FK_tokens_tenants_TenantId",
                table: "tokens");

            migrationBuilder.DropIndex(
                name: "IX_tokens_TenantId",
                table: "tokens");

            migrationBuilder.DropIndex(
                name: "IX_corpora_TenantId_Name",
                table: "corpora");

            // corpus_grants references tenants, so it goes before the table it points at.
            migrationBuilder.DropTable(
                name: "corpus_grants");

            // The columns go BEFORE the table they reference. Dropping tenants first, which
            // is what EF scaffolded, leaves corpora and tokens holding TenantId values that
            // point at rows being deleted, and SQLite refuses with a foreign key error.
            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "tokens");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "corpora");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "corpora");

            migrationBuilder.CreateTable(
                name: "admin_credential",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    PasswordHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PasswordSalt = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_credential", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "token_corpora",
                columns: table => new
                {
                    TokenId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CorpusId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_token_corpora", x => new { x.TokenId, x.CorpusId });
                    table.ForeignKey(
                        name: "FK_token_corpora_corpora_CorpusId",
                        column: x => x.CorpusId,
                        principalTable: "corpora",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_token_corpora_tokens_TokenId",
                        column: x => x.TokenId,
                        principalTable: "tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_corpora_Name",
                table: "corpora",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_token_corpora_CorpusId",
                table: "token_corpora",
                column: "CorpusId");

            // LAST. EF defers the SQLite table rebuilds that dropping a column needs, and
            // PRAGMA foreign_keys is a no-op inside a transaction, so a drop placed earlier
            // runs while corpora and tokens still carry TenantId and SQLite refuses it. The
            // operations above flush the pending rebuild before this point.
            migrationBuilder.DropTable(
                name: "tenants");

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_credential");

            migrationBuilder.DropTable(
                name: "token_corpora");

            migrationBuilder.DropIndex(
                name: "IX_corpora_Name",
                table: "corpora");

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "tokens",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "corpora",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Visibility",
                table: "corpora",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "corpus_grants",
                columns: table => new
                {
                    CorpusId = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_corpus_grants", x => new { x.CorpusId, x.TenantId });
                    table.ForeignKey(
                        name: "FK_corpus_grants_corpora_CorpusId",
                        column: x => x.CorpusId,
                        principalTable: "corpora",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_corpus_grants_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tokens_TenantId",
                table: "tokens",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_corpora_TenantId_Name",
                table: "corpora",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_corpus_grants_TenantId",
                table: "corpus_grants",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_corpora_tenants_TenantId",
                table: "corpora",
                column: "TenantId",
                principalTable: "tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_tokens_tenants_TenantId",
                table: "tokens",
                column: "TenantId",
                principalTable: "tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
