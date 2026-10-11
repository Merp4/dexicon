using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <summary>
    /// Store whether a key reaches every corpus as a column of its own, <c>tokens.AllCorpora</c>.
    ///
    /// A key used to reach every corpus when it had no rows in <c>token_corpora</c>. A corpus
    /// removal deletes the rows that name it, so a key restricted to one corpus had no rows once
    /// that corpus was removed, and reached every corpus from then on. The column makes the two
    /// states different: a key restricted to nothing is <c>AllCorpora = 0</c> with no rows.
    ///
    /// Existing keys keep what they reach today. A key with no rows becomes <c>AllCorpora = 1</c>,
    /// and a key with rows stays restricted to them. The statement reads the rows as they are, so it
    /// is correct only on the catalogue as it was before this column existed and runs once, from the
    /// migration history. A key with no rows at that moment, including one that a removal had
    /// already emptied, is indistinguishable from an unrestricted key and is made one.
    ///
    /// <c>Down</c> drops the column. An earlier version reads a key with no rows as unrestricted, so
    /// a key restricted to nothing then reaches every corpus again. It also ignores the column: a key
    /// it creates has <c>AllCorpora = 0</c> and no rows, and a key it restricts keeps its old value.
    /// Upgrading again does not repeat the backfill, so a key created or changed in between has to be
    /// checked on the Access page. Restoring the backup taken before the upgrade avoids that.
    /// </summary>
    public partial class KeyReachIsExplicit : Migration
    {
        /// <summary>The statement that sets the column for the keys that exist.</summary>
        private const string BackfillSql = @"
                UPDATE tokens
                SET AllCorpora = 1
                WHERE NOT EXISTS (SELECT 1 FROM token_corpora WHERE token_corpora.TokenId = tokens.Id);";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllCorpora",
                table: "tokens",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllCorpora",
                table: "tokens");
        }
    }
}
