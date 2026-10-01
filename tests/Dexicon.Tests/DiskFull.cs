using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Dexicon.Tests;

/// <summary>
/// Refuses catalogue writes the way a full data disk does: SQLite error 13, "database or
/// disk is full". Closed until a test opens it, so the seed's own writes are never refused,
/// and the tests assert it was reached: a window that quietly stops opening lets every
/// assertion about the recovery hold while nothing was being recovered from.
///
/// Three shapes of outage. <see cref="RefuseSaves"/> refuses a number of saves and then
/// recovers, which is a fault shorter than the retries. <see cref="Fill"/> refuses every
/// write, a lease claim or release included, until <see cref="Free"/>, which is the outage
/// that outlasts them. <see cref="RefuseWritesTo"/> refuses the write to one table, which
/// lands in the middle of a save whichever order its rows go in, and shows whether the rows
/// before it were kept.
///
/// A refused save is refused before it reaches the database, so nothing of it is written.
/// A refused command inside a save rolls the save back, as SQLite does.
/// </summary>
internal sealed class DiskFull : IDbCommandInterceptor, ISaveChangesInterceptor
{
    private int _savesToRefuse;
    private volatile bool _full;
    private volatile string? _table;
    private int _savesRefused;
    private int _writesRefused;

    /// <summary>Saves refused, which is one per attempt at saving.</summary>
    public int SavesRefused => Volatile.Read(ref _savesRefused);

    /// <summary>Other writes refused: lease claims and releases while full, or the table's write.</summary>
    public int WritesRefused => Volatile.Read(ref _writesRefused);

    public bool Fired => SavesRefused + WritesRefused > 0;

    /// <summary>The next <paramref name="count"/> saves fail, then writes work again.</summary>
    public void RefuseSaves(int count) => Volatile.Write(ref _savesToRefuse, count);

    /// <summary>Every write fails until <see cref="Free"/>.</summary>
    public void Fill() => _full = true;

    /// <summary>Writes to <paramref name="table"/> fail until <see cref="Free"/>.</summary>
    public void RefuseWritesTo(string table) => _table = table;

    public void Free()
    {
        _full = false;
        _table = null;
        Volatile.Write(ref _savesToRefuse, 0);
    }

    /// <summary>The error a full disk raises, for a test that fails a pass with it.</summary>
    public static SqliteException Refusal() =>
        new("SQLite Error 13: 'database or disk is full'.", 13);

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (_full || TakeRefusal())
        {
            Interlocked.Increment(ref _savesRefused);
            throw Refusal();
        }

        return ValueTask.FromResult(result);
    }

    public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Observe(command);
        return ValueTask.FromResult(result);
    }

    public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Observe(command);
        return ValueTask.FromResult(result);
    }

    private void Observe(DbCommand command)
    {
        var text = command.CommandText;
        if (!IsWrite(text)) return;

        var table = _table;
        if (_full || (table is not null && text.Contains(table, StringComparison.OrdinalIgnoreCase)))
        {
            Interlocked.Increment(ref _writesRefused);
            throw Refusal();
        }
    }

    private bool TakeRefusal()
    {
        while (true)
        {
            var left = Volatile.Read(ref _savesToRefuse);
            if (left <= 0) return false;
            if (Interlocked.CompareExchange(ref _savesToRefuse, left - 1, left) == left) return true;
        }
    }

    private static bool IsWrite(string text) =>
        text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase);
}
