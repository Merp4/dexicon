using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Core.Catalog;

/// <summary>What a failed catalogue save says about why it failed.</summary>
internal static class SqliteViolations
{
    private const int Constraint = 19;            // SQLITE_CONSTRAINT
    private const int PrimaryKeyViolation = 1555; // SQLITE_CONSTRAINT_PRIMARYKEY
    private const int UniqueViolation = 2067;     // SQLITE_CONSTRAINT_UNIQUE
    private const int ForeignKeyViolation = 787;  // SQLITE_CONSTRAINT_FOREIGNKEY

    /// <summary>
    /// Whether a failed save was refused for a key already taken: a primary-key or unique violation.
    /// It does not say which key, so a caller that needs the other writer's row confirms that it is
    /// there (<c>DocumentService</c> does; <c>ExtractedTextCache</c> does not, because its table has
    /// one key and a lost race changes nothing). A busy or full database, or a broken connection, is
    /// not a duplicate key and stays an error.
    /// </summary>
    public static bool IsDuplicateKey(this DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: Constraint } inner
        && inner.SqliteExtendedErrorCode is PrimaryKeyViolation or UniqueViolation;

    /// <summary>
    /// Whether a failed save named a row that is not there: a foreign-key violation, such as a file inserted
    /// under a source that was deleted meanwhile.
    /// </summary>
    public static bool IsForeignKeyViolation(this DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: Constraint } inner
        && inner.SqliteExtendedErrorCode is ForeignKeyViolation;
}
