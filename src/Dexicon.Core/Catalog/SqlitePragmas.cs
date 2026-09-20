using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Dexicon.Core.Catalog;

/// <summary>
/// Applies the catalogue's journal mode and busy timeout to every connection, and checks
/// that they took.
///
/// Neither is established by the connection string, and neither was set anywhere else.
/// What that produced was measured rather than assumed: a database created by
/// <c>Data Source=…</c> reports <c>journal_mode=delete</c> and <c>busy_timeout=0</c>. The
/// deployed catalogue is in WAL regardless, because journal mode is persistent once set
/// and that file happens to carry it, so a fresh install and a long-lived one behaved
/// differently under concurrent access with nothing in the code to say so.
///
/// Rollback journalling is the worse of the two here: a reader blocks a writer, so the UI
/// polling the catalogue contends with an index job writing it. WAL is stated explicitly
/// so a new deployment gets what the old one has.
///
/// The busy timeout makes SQLite wait inside the call. Without it the wait still happened,
/// because Microsoft.Data.Sqlite retries a busy database for the command timeout, but it
/// happened as a retry loop rather than a block: measured against a lock held for 500ms,
/// both arrangements succeeded in about 600ms. What it changes is the long hold, where a
/// blocked writer waits on the lock being released instead of spinning until the command
/// timeout expires.
/// </summary>
public sealed class SqlitePragmas(TimeSpan busyTimeout, ILogger<SqlitePragmas> log)
    : DbConnectionInterceptor
{
    // Static: a new interceptor is constructed for every DbContext, so an instance flag
    // never survived and this reported on every connection. The log was a line every few
    // seconds, which buries everything else.
    private static int _reported;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Apply(connection);

    public override Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Apply(connection);
        return Task.CompletedTask;
    }

    private void Apply(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite) return;

        // An in-memory database is one connection's private store and there is nothing to
        // contend with it, so neither pragma buys anything and WAL is not available.
        if (sqlite.DataSource is ":memory:") return;

        Execute(sqlite, $"PRAGMA busy_timeout={(int)busyTimeout.TotalMilliseconds};");
        Execute(sqlite, "PRAGMA journal_mode=WAL;");

        // Once per process. Asserting the pragmas took is the point of setting them
        // explicitly: WAL is refused on some filesystems, notably network shares, and a
        // silent fall back to rollback journalling is exactly the difference this exists
        // to remove.
        if (Interlocked.Exchange(ref _reported, 1) == 1) return;

        var mode = Scalar(sqlite, "PRAGMA journal_mode;");
        var busy = Scalar(sqlite, "PRAGMA busy_timeout;");

        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning(
                "Catalogue is in journal mode {Mode}, not WAL. It was asked for and refused, "
                + "which happens on filesystems that cannot do it. Readers will block writers.",
                mode);
        }
        else
        {
            log.LogInformation("Catalogue journal mode {Mode}, busy timeout {Busy}ms", mode, busy);
        }
    }

    private static void Execute(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString();
    }
}
