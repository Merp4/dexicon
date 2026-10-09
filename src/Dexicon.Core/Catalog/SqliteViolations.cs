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
    /// Whether a failed save was refused for a key already taken: a primary-key or unique violation.
    /// It does not say which key, so a caller that acts on the other writer having won confirms that
    /// the row is there. A busy or full database, or a broken connection, is not a duplicate key and
    /// stays an error.
    /// </summary>
    public static bool IsDuplicateKey(this DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: Constraint } inner
        && inner.SqliteExtendedErrorCode is PrimaryKeyViolation or UniqueViolation;
}
