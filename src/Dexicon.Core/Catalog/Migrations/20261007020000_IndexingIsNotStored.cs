using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <summary>
    /// Stop storing Indexing on corpora and chunk sets.
    ///
    /// A job used to write Indexing when it started and clear it when it finished, and one that
    /// could not save its outcome left it set for ever. It is read now, from the jobs and the
    /// corpus lease (<c>IndexingActivity</c>), so no row should hold it. A row that does was
    /// left by a job that stopped partway, and what it found is not known, so it reads
    /// Degraded until the next pass says otherwise.
    ///
    /// Data only. No schema changes, which is why <c>Down</c> cannot restore anything: which
    /// rows were Indexing is not recorded anywhere, and none of them still is.
    /// </summary>
    public partial class IndexingIsNotStored : Migration
    {
        /// <summary>
        /// The statements, named so a test can run the same text this migration runs rather
        /// than a copy of it that can drift away from it. The column holds the enum's name.
        /// </summary>
        internal const string ClearStoredIndexingSql = @"
                UPDATE corpora SET State = 'Degraded' WHERE State = 'Indexing';
                UPDATE chunk_sets SET State = 'Degraded' WHERE State = 'Indexing';";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(ClearStoredIndexingSql);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. Nothing records which rows were Indexing, and a row marked
            // Indexing with no job behind it is the state this migration exists to end.
        }
    }
}
