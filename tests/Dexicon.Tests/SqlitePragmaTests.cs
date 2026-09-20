using Dexicon.Core.Catalog;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// The catalogue's journal mode and busy timeout, which nothing established before.
///
/// Measured rather than reasoned about, because the obvious readings of the connection
/// string were both wrong: a database created by `Data Source=…` reports
/// `journal_mode=delete` and `busy_timeout=0`, while the deployed catalogue is in WAL
/// anyway because journal mode is persistent in the file. A fresh install and a long-lived
/// one therefore behaved differently with nothing in the code to say so.
/// </summary>
public sealed class SqlitePragmaTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"pragma-{Guid.NewGuid():N}.db");

    private CatalogDbContext Context() =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite($"Data Source={_path}")
            .AddInterceptors(new SqlitePragmas(
                TimeSpan.FromSeconds(7), NullLogger<SqlitePragmas>.Instance))
            .Options);

    private static string? Scalar(CatalogDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString();
    }

    [Fact]
    public void TheCatalogueIsInWalRatherThanWhateverTheFileCameWith()
    {
        // Without this the mode is `delete`, where a reader blocks a writer: the UI polling
        // the catalogue contends with an index job writing it.
        using var db = Context();
        db.Database.EnsureCreated();

        Scalar(db, "PRAGMA journal_mode;").ShouldBe("wal", StringCompareShould.IgnoreCase);
    }

    [Fact]
    public void TheBusyTimeoutIsTheConfiguredOne()
    {
        // It is 0 by default, which is not a fast failure but no waiting inside SQLite at
        // all: the wait happened in the provider's retry loop instead.
        using var db = Context();
        db.Database.EnsureCreated();

        Scalar(db, "PRAGMA busy_timeout;").ShouldBe("7000");
    }

    [Fact]
    public void AnInMemoryDatabaseIsLeftAlone()
    {
        // Every test in this suite runs on `:memory:`, which is one connection's private
        // store with nothing to contend with it and no WAL available. Asking anyway threw
        // on some providers, so it is skipped rather than attempted and warned about.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new SqlitePragmas(
                TimeSpan.FromSeconds(7), NullLogger<SqlitePragmas>.Instance))
            .Options);

        Should.NotThrow(() => db.Database.EnsureCreated());
        Scalar(db, "PRAGMA journal_mode;").ShouldBe("memory", StringCompareShould.IgnoreCase);
    }

    [Fact]
    public async Task AWriterWaitsOutALockRatherThanFailingOnIt()
    {
        // The property the settings exist for. A second connection meeting a lock held
        // briefly must block and then succeed; the measurement that motivated this is a
        // 500ms hold, which is the shape of a batched SaveChanges.
        using var db = Context();
        db.Database.EnsureCreated();

        using var holder = new SqliteConnection($"Data Source={_path}");
        holder.Open();
        using var tx = holder.BeginTransaction();
        using (var w = holder.CreateCommand())
        {
            w.CommandText = "CREATE TABLE IF NOT EXISTS probe(x); INSERT INTO probe VALUES (1);";
            w.Transaction = tx;
            w.ExecuteNonQuery();
        }

        var released = Task.Run(async () => { await Task.Delay(400); tx.Commit(); });

        using var second = new SqliteConnection($"Data Source={_path}");
        second.Open();
        using (var busy = second.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout=7000;";
            busy.ExecuteNonQuery();
        }

        using var write = second.CreateCommand();
        write.CommandText = "CREATE TABLE IF NOT EXISTS probe2(x); INSERT INTO probe2 VALUES (2);";
        Should.NotThrow(() => write.ExecuteNonQuery());

        await released;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_path + suffix); } catch (IOException) { /* a pooled handle */ }
        }
    }
}
