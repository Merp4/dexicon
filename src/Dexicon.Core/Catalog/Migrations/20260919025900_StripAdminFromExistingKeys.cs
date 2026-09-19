using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <summary>
    /// Rewrite keys issued before D-28 so their stored scopes match what they can do.
    ///
    /// <c>TokenService</c> strips <c>admin</c> when it builds a principal, so such a key is
    /// already refused every admin endpoint. What it does not do is rewrite the row, so the
    /// Access page read the raw column and displayed a scope the key demonstrably could not
    /// use. Found by migrating a real catalogue and then asking the running server what the
    /// key could actually reach.
    ///
    /// Data only. No schema changes, which is why <c>Down</c> cannot restore anything: the
    /// scopes a key held before this ran are not recorded anywhere, and inventing them back
    /// would be worse than leaving them off.
    /// </summary>
    public partial class StripAdminFromExistingKeys : Migration
    {
        /// <summary>
        /// The statement, named so a test can run the same text this migration runs rather
        /// than a copy of it that can drift away from it.
        ///
        /// Wrapping the list in commas means one pattern handles `admin` first, last,
        /// middle and alone, rather than three near-identical replacements that each miss a
        /// position. Spaces go first, because the column is written by string.Join(',') but
        /// was hand-editable before that.
        ///
        /// A key whose only scope was `admin` is left with none. That is faithful: it is
        /// exactly what the key can already do, since the scope was being stripped at
        /// verification anyway. Reissue it rather than editing the row.
        /// </summary>
        internal const string StripAdminSql = @"
                UPDATE tokens
                   SET Scopes = TRIM(
                           REPLACE(',' || REPLACE(Scopes, ' ', '') || ',', ',admin,', ','),
                           ',')
                 WHERE ',' || REPLACE(Scopes, ' ', '') || ',' LIKE '%,admin,%';";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(StripAdminSql);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. Granting `admin` back to every key would hand out a scope
            // no key is allowed to hold, and there is no record of which ones had it.
        }
    }
}
