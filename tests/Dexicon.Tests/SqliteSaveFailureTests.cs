using Dexicon.Core.Catalog;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>What a failed catalogue save says, and how many rows it names.</summary>
public sealed class SqliteSaveFailureTests
{
    [Fact]
    public async Task ASaveWhoseRowsWereAllDeletedNamesOneOfThem()
    {
        // The pass drops one file per failed save, which is why it saves again after each drop.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using (var setup = harness.NewContext())
        {
            var documents = harness.NewDocumentService(setup);
            var corpus = await setup.Corpora.SingleAsync();
            for (var i = 0; i < 3; i++)
            {
                var stored = await documents.StoreAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"doc {i}")), $"d{i}.txt");
                await documents.AttachAsync(corpus, stored.Sha256, $"d{i}.txt");
            }
        }

        await using var db = harness.NewContext();
        foreach (var state in await db.FileChunkStates.ToListAsync()) state.Status = FileStatus.Indexed;
        await using (var other = harness.NewContext())
            await other.Files.ExecuteDeleteAsync();

        var failure = await Should.ThrowAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());

        failure.Entries.Count.ShouldBe(1, "three rows were deleted and the save names the first");
    }

    [Theory]
    [InlineData(19, 787, true)]    // SQLITE_CONSTRAINT_FOREIGNKEY
    [InlineData(19, 1555, false)]  // SQLITE_CONSTRAINT_PRIMARYKEY
    [InlineData(19, 2067, false)]  // SQLITE_CONSTRAINT_UNIQUE
    [InlineData(5, 5, false)]      // SQLITE_BUSY
    [InlineData(13, 13, false)]    // SQLITE_FULL
    public void OnlyAMissingParentIsAForeignKeyViolation(int code, int extendedCode, bool expected)
    {
        var failure = new DbUpdateException("save failed", new SqliteException("SQLite Error", code, extendedCode));

        failure.IsForeignKeyViolation().ShouldBe(expected);
    }

    [Fact]
    public void AFailureWithNoDatabaseErrorBehindItIsNotAForeignKeyViolation()
    {
        new DbUpdateException("save failed").IsForeignKeyViolation().ShouldBeFalse();
    }
}
