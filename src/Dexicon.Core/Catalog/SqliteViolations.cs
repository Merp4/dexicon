using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Core.Catalog;

/// <summary>What a failed catalogue save says about why it failed.</summary>
internal static class SqliteViolations
{
    private const int Constraint = 19;            // SQLITE_CONSTRAINT
    private const int PrimaryKeyViolation = 1555; // SQLITE_CONSTRAINT_PRIMARYKEY
    private const int UniqueViolation = 2067;     // SQLITE_CONSTRAINT_UNIQUE

    /// <summary>
    /// Whether a failed write was another writer getting there first, rather than the
    /// database being unable to take it.
    ///
    /// Filtering on "the failing entry was a FileText" is not the same question. A busy
    /// database, a full disk or a broken connection all fail on that entry too, and with
    /// several jobs writing concurrently SQLITE_BUSY is no longer unlikely. Treated as a
    /// harmless collision they would be logged at debug and the row silently not stored,
    /// so every later pass would re-extract the file and the real fault would never
    /// surface.
    /// </summary>
    public static bool IsDuplicateKey(this DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: Constraint } inner
        && inner.SqliteExtendedErrorCode is PrimaryKeyViolation or UniqueViolation;
}
