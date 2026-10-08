using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <summary>
    /// Give every key that holds <c>ingest</c> the new <c>destroy</c> scope.
    ///
    /// Detaching a document from a corpus used to need <c>ingest</c>. It needs <c>destroy</c> now
    /// (docs/decisions.md D-40), so a key that could detach yesterday would otherwise be refused
    /// today. Granting it once, here, keeps every existing key doing what it did. A key issued
    /// after this holds only what it was given, and one adopted from the environment is not
    /// given it (<c>Scopes.Bootstrap</c>).
    ///
    /// Data only. No schema changes, and <c>Down</c> restores nothing: afterwards there is no
    /// telling a key that was given <c>destroy</c> here from one given it on the Access page.
    /// A running 0.6.6 treats the extra word as an unknown scope and ignores it.
    /// </summary>
    public partial class GrantDestroyToIngestKeys : Migration
    {
        /// <summary>
        /// The statement, named so a test can run the same text this migration runs. The scopes
        /// are a comma-separated list, matched whole by padding both sides, so a longer word that
        /// contains <c>ingest</c> is not mistaken for it. A key that already holds <c>destroy</c>
        /// is left as it is, which makes a second run change nothing.
        /// </summary>
        internal const string GrantSql = @"
                UPDATE tokens
                SET Scopes = Scopes || ',destroy'
                WHERE (',' || Scopes || ',') LIKE '%,ingest,%'
                  AND (',' || Scopes || ',') NOT LIKE '%,destroy,%';";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(GrantSql);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. Nothing records which keys were given destroy here, and removing
            // it from every key would also remove it from those it was given on purpose.
        }
    }
}
