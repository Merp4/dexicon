using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <summary>
    /// Fold the saved framings and measurements stored under <c>model:latest</c> into the row for
    /// <c>model</c>.
    ///
    /// Ollama lists <c>embeddinggemma:latest</c>, the Models screen sent that name to save a framing
    /// and to probe, and a chunk set records <c>embeddinggemma</c>. Both tables are keyed on
    /// (provider, model), so the two spellings were two rows, and the indexer read the one the set
    /// names: a framing saved from the row never applied, and a measurement taken from it was not
    /// found. Both are now written and read under the name without <c>:latest</c>, which
    /// <c>EmbeddingTarget.Canonical</c> produces; this moves the rows already stored under the old
    /// spelling.
    ///
    /// Where a model has both rows the newer one is kept (<c>UpdatedUtc</c> for a framing,
    /// <c>MeasuredUtc</c> for a measurement), and the older is dropped. A saved framing changes the
    /// chunking fingerprint of every set on its model, so a set that had been indexing under the
    /// built-in framing re-embeds on its next pass.
    ///
    /// Data only. No schema changes, and <c>Down</c> restores nothing: afterwards there is no telling
    /// which rows were stored tagged.
    /// </summary>
    public partial class FoldTaggedModelNames : Migration
    {
        /// <summary>
        /// The statements, named so a test can run the text this migration runs. A name matches
        /// when it ends in <c>:latest</c> in any case (the rule of <c>EmbeddingTarget.Canonical</c>),
        /// so <c>:v1.5</c> is a different model and is left alone. Within one provider the rows for
        /// one canonical name are ranked by time, then by name, so exactly one survives; the rest
        /// are deleted before the survivor is renamed, which keeps the (provider, model) key
        /// unique. A second run finds no name ending in <c>:latest</c> and changes nothing.
        /// </summary>
        internal const string FoldSql = @"
                DELETE FROM model_profiles AS p
                WHERE EXISTS (
                    SELECT 1 FROM model_profiles AS o
                    WHERE o.Provider = p.Provider
                      AND o.Model <> p.Model
                      AND (CASE WHEN o.Model LIKE '%:latest' THEN substr(o.Model, 1, length(o.Model) - 7) ELSE o.Model END)
                        = (CASE WHEN p.Model LIKE '%:latest' THEN substr(p.Model, 1, length(p.Model) - 7) ELSE p.Model END)
                      AND (o.UpdatedUtc > p.UpdatedUtc OR (o.UpdatedUtc = p.UpdatedUtc AND o.Model < p.Model)));

                UPDATE model_profiles
                SET Model = substr(Model, 1, length(Model) - 7)
                WHERE Model LIKE '%:latest';

                DELETE FROM model_measurements AS p
                WHERE EXISTS (
                    SELECT 1 FROM model_measurements AS o
                    WHERE o.Provider = p.Provider
                      AND o.Model <> p.Model
                      AND (CASE WHEN o.Model LIKE '%:latest' THEN substr(o.Model, 1, length(o.Model) - 7) ELSE o.Model END)
                        = (CASE WHEN p.Model LIKE '%:latest' THEN substr(p.Model, 1, length(p.Model) - 7) ELSE p.Model END)
                      AND (o.MeasuredUtc > p.MeasuredUtc OR (o.MeasuredUtc = p.MeasuredUtc AND o.Model < p.Model)));

                UPDATE model_measurements
                SET Model = substr(Model, 1, length(Model) - 7)
                WHERE Model LIKE '%:latest';";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(FoldSql);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. Nothing records which rows were stored tagged, and the older row
            // of a pair is gone.
        }
    }
}
