using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <summary>
    /// Fold the saved framings and measurements stored under <c>Model:latest</c> into the row for
    /// <c>model</c>.
    ///
    /// Ollama lists <c>embeddinggemma:latest</c>, the Models screen sent that name to save a framing
    /// and to probe, and a chunk set records <c>embeddinggemma</c>. Both tables are keyed on
    /// (provider, model), so the two spellings were two rows, and the indexer read the one the set
    /// names: a framing saved from the row never applied, and a measurement taken from it was not
    /// found. The same held for a name typed in another letter case. Both are now written and read
    /// under the lower-case name without <c>:latest</c>, which <c>EmbeddingTarget.Canonical</c>
    /// produces; this moves the rows already stored under any other spelling.
    ///
    /// Where a model has several rows the newest is kept (<c>UpdatedUtc</c> for a framing,
    /// <c>MeasuredUtc</c> for a measurement), and the others are dropped. A saved framing changes the
    /// chunking fingerprint of every set on its model, so a set that had been indexing under the
    /// built-in framing re-embeds on its next pass.
    ///
    /// Data only. No schema changes, and <c>Down</c> restores nothing: afterwards there is no telling
    /// which rows were stored under another spelling.
    /// </summary>
    public partial class FoldTaggedModelNames : Migration
    {
        /// <summary>
        /// The statements, named so a test can run the text this migration runs. A name is reduced
        /// the way <c>EmbeddingTarget.Canonical</c> reduces it: <c>:latest</c> removed when it ends
        /// the name in any case, then lower-cased, so <c>:v1.5</c> is a different model and is left
        /// alone. SQLite's <c>lower()</c> folds ASCII only, which covers every model name a provider
        /// publishes. Within one provider the rows for one reduced name are ranked by time, then by
        /// name, so exactly one survives; the rest are deleted before the survivor is renamed, which
        /// keeps the (provider, model) key unique. A second run finds every name already reduced and
        /// changes nothing.
        /// </summary>
        internal static readonly string FoldSql =
            Fold("model_profiles", "UpdatedUtc") + Fold("model_measurements", "MeasuredUtc");

        private static string Reduced(string model) =>
            $"lower(CASE WHEN {model} LIKE '%:latest' THEN substr({model}, 1, length({model}) - 7) ELSE {model} END)";

        private static string Fold(string table, string time) => $@"
                DELETE FROM {table} AS p
                WHERE EXISTS (
                    SELECT 1 FROM {table} AS o
                    WHERE o.Provider = p.Provider
                      AND o.Model <> p.Model
                      AND {Reduced("o.Model")} = {Reduced("p.Model")}
                      AND (o.{time} > p.{time} OR (o.{time} = p.{time} AND o.Model < p.Model)));

                UPDATE {table}
                SET Model = {Reduced("Model")}
                WHERE Model <> {Reduced("Model")};
";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(FoldSql);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. Nothing records which rows were stored under another spelling, and
            // the older rows of a group are gone.
        }
    }
}
