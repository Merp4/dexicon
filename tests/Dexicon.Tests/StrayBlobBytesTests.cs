using Dexicon.Core.Catalog;
using Dexicon.Core.Documents;
using Dexicon.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dexicon.Tests;

/// <summary>
/// The files under the blob root that no document names are deleted at startup: an upload's temporary file left by
/// a crash, and bytes whose blob row is gone. Nothing else under the root is touched, a link is not followed, and a
/// catalogue with no documents at all leaves hash-named files alone, since it may be the wrong catalogue.
/// </summary>
public sealed class StrayBlobBytesTests
{
    private const string Hex64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string HashFile(string root, string sha, string content = "bytes")
    {
        var path = Path.Combine(root, sha[..2], sha);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Temporary(string root, string content = "partial")
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $".incoming-{Guid.NewGuid():N}");
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<(IndexingHarness Harness, string Root, CatalogDbContext Db)> StartWithADocumentAsync()
    {
        var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var stored = await documents.StoreAsync(new MemoryStream("a document that is held"u8.ToArray()), "a.txt");
        await documents.AttachAsync(await db.Corpora.SingleAsync(), stored.Sha256, "a.txt");
        return (harness, harness.Settings.Value.Storage.BlobRoot, db);
    }

    [Fact]
    public async Task ATemporaryFileAndBytesNoDocumentNamesAreDeletedAndAHeldDocumentIsKept()
    {
        var (harness, root, db) = await StartWithADocumentAsync();
        await using var _ = harness;
        await using var __ = db;
        var held = db.Blobs.AsNoTracking().Single().Sha256;
        var temporary = Temporary(root);
        var stray = HashFile(root, Hex64, "twelve bytes");

        var swept = await StrayBlobBytes.SweepAsync(db, root);

        swept.ShouldBe(new StraySweep(Deleted: 2, Bytes: 7 + 12, Failed: 0, KeptForEmptyCatalogue: 0));
        File.Exists(temporary).ShouldBeFalse();
        File.Exists(stray).ShouldBeFalse();
        File.Exists(Path.Combine(root, held[..2], held)).ShouldBeTrue("the bytes of a document a corpus holds");
    }

    [Fact]
    public async Task OnlyTheTwoShapesOfFileAreTouched()
    {
        var (harness, root, db) = await StartWithADocumentAsync();
        await using var _ = harness;
        await using var __ = db;
        var left = new List<string>
        {
            // Directly under the root, but not an upload's temporary file.
            Path.Combine(root, "notes.txt"),
            Path.Combine(root, ".incoming-short"),
            Path.Combine(root, $".incoming-{new string('A', 32)}"),
            Path.Combine(root, ".incoming-" + new string('a', 33)),
            // A hash-named file in the wrong folder, in a folder that is not a fan-out, or named by a hash that is
            // too short, too long or upper case.
            Path.Combine(root, "ff", Hex64),
            Path.Combine(root, "012", Hex64[..64]),
            Path.Combine(root, "01", Hex64[..63]),
            Path.Combine(root, "01", Hex64 + "0"),
            Path.Combine(root, "01", Hex64.ToUpperInvariant()),
            Path.Combine(root, "01", "README"),
            // Deeper than the fan-out folder.
            Path.Combine(root, "01", "nested", Hex64),
            Path.Combine(root, "other", "readme.txt"),
        };
        foreach (var path in left)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "not ours");
        }

        var swept = await StrayBlobBytes.SweepAsync(db, root);

        swept.Deleted.ShouldBe(0);
        left.Where(path => !File.Exists(path)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AFolderNamedLikeATemporaryFileOrAHashIsNotDeleted()
    {
        var (harness, root, db) = await StartWithADocumentAsync();
        await using var _ = harness;
        await using var __ = db;
        var temporaryFolder = Path.Combine(root, $".incoming-{Guid.NewGuid():N}");
        var hashFolder = Path.Combine(root, "01", Hex64);
        Directory.CreateDirectory(temporaryFolder);
        Directory.CreateDirectory(hashFolder);
        File.WriteAllText(Path.Combine(hashFolder, "inside"), "kept");

        var swept = await StrayBlobBytes.SweepAsync(db, root);

        swept.Deleted.ShouldBe(0);
        Directory.Exists(temporaryFolder).ShouldBeTrue();
        File.Exists(Path.Combine(hashFolder, "inside")).ShouldBeTrue();
    }

    [Fact]
    public async Task ACatalogueWithNoDocumentsLeavesTheHashNamedFilesAndWarns()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        var root = harness.Settings.Value.Storage.BlobRoot;
        await using var db = harness.NewContext();
        var temporary = Temporary(root);
        var named = HashFile(root, Hex64);
        var logs = new RecordingLoggerFactory();

        var swept = await StrayBlobBytes.SweepAsync(db, root, logs.CreateLogger("sweep"));

        swept.ShouldBe(new StraySweep(Deleted: 1, Bytes: 7, Failed: 0, KeptForEmptyCatalogue: 1));
        File.Exists(temporary).ShouldBeFalse("a temporary file is deleted whatever the catalogue holds");
        File.Exists(named).ShouldBeTrue("the catalogue may be another one, and this the only copy");
        logs.Levels.ShouldBe([LogLevel.Warning]);
        logs.Lines.ShouldHaveSingleItem().ShouldContain("another catalogue");
    }

    [Fact]
    public async Task ARootThatDoesNotExistIsNothingToSweep()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();

        var swept = await StrayBlobBytes.SweepAsync(db, Path.Combine(harness.DataPath, "no-such-blobs"));

        swept.ShouldBe(default);
    }

    [Fact]
    public async Task FilesThatCannotBeDeletedAreCountedAndLoggedOnceAndTheRestAreStillDeleted()
    {
        var (harness, root, db) = await StartWithADocumentAsync();
        await using var _ = harness;
        await using var __ = db;
        var first = Temporary(root, "locked");
        var second = Temporary(root, "locked too");
        var other = Temporary(root, "other");
        var logs = new RecordingLoggerFactory();

        // An open handle without share-delete refuses a delete on Windows. On Linux the delete succeeds, so there
        // the assertion is only that nothing is reported as failed.
        await using var handle1 = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var handle2 = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read);
        var swept = await StrayBlobBytes.SweepAsync(db, root, logs.CreateLogger("sweep"));

        File.Exists(other).ShouldBeFalse();
        if (OperatingSystem.IsWindows())
        {
            swept.Failed.ShouldBe(2);
            swept.Deleted.ShouldBe(1);
            File.Exists(first).ShouldBeTrue();
            File.Exists(second).ShouldBeTrue();
            logs.Levels.ShouldBe([LogLevel.Warning], "one line for the run, not one for each file");
        }
        else
        {
            swept.Failed.ShouldBe(0);
            swept.Deleted.ShouldBe(3);
        }
    }

    [UnprivilegedLinuxFact]
    public async Task AFolderThatCannotBeListedIsLoggedOnceAndTheOthersAreStillSwept()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("permission bits refuse a listing on Linux only");

        var (harness, root, db) = await StartWithADocumentAsync();
        await using var _ = harness;
        await using var __ = db;
        var hidden = HashFile(root, "aa" + new string('0', 62));
        var readable = HashFile(root, "bb" + new string('0', 62));
        var temporary = Temporary(root);
        var logs = new RecordingLoggerFactory();
        var hiddenFolder = Path.GetDirectoryName(hidden)!;
        File.SetUnixFileMode(hiddenFolder, UnixFileMode.None);
        try
        {
            var swept = await StrayBlobBytes.SweepAsync(db, root, logs.CreateLogger("sweep"));

            swept.Deleted.ShouldBe(2);
            File.Exists(readable).ShouldBeFalse();
            File.Exists(temporary).ShouldBeFalse();
            logs.Levels.ShouldBe([LogLevel.Warning]);
        }
        finally
        {
            File.SetUnixFileMode(hiddenFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        File.Exists(hidden).ShouldBeTrue("a folder that could not be listed is left as it was");
    }
    [LinuxFact]
    public async Task ALinkIsNeitherFollowedNorDeleted()
    {
        var (harness, root, db) = await StartWithADocumentAsync();
        await using var _ = harness;
        await using var __ = db;
        var outside = Path.Combine(harness.DataPath, "outside");
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "target");
        File.WriteAllText(target, "outside the blob root");
        var linkedTemporary = Path.Combine(root, $".incoming-{Guid.NewGuid():N}");
        File.CreateSymbolicLink(linkedTemporary, target);
        var linkedName = Path.Combine(root, "01", Hex64);
        Directory.CreateDirectory(Path.GetDirectoryName(linkedName)!);
        File.CreateSymbolicLink(linkedName, target);
        var linkedFolder = Path.Combine(root, "02");
        var folderTarget = Path.Combine(outside, "folder");
        Directory.CreateDirectory(folderTarget);
        File.WriteAllText(Path.Combine(folderTarget, "0200000000000000000000000000000000000000000000000000000000000000"), "outside");
        Directory.CreateSymbolicLink(linkedFolder, folderTarget);

        var swept = await StrayBlobBytes.SweepAsync(db, root);

        swept.Deleted.ShouldBe(0);
        File.Exists(target).ShouldBeTrue();
        File.Exists(Path.Combine(folderTarget, "0200000000000000000000000000000000000000000000000000000000000000")).ShouldBeTrue();
        new FileInfo(linkedTemporary).LinkTarget.ShouldNotBeNull("the link itself is left");
    }

    [Fact]
    public async Task StartupSweepsTheBlobRoot()
    {
        var (harness, root, db) = await StartWithADocumentAsync();
        await using var _ = harness;
        await using var __ = db;
        var temporary = Temporary(root);
        var logs = new RecordingLoggerFactory();

        await Bootstrapper.SweepStrayBlobBytesAsync(db, harness.Settings.Value, logs.CreateLogger("startup"));

        File.Exists(temporary).ShouldBeFalse();
        logs.Lines.ShouldHaveSingleItem().ShouldContain("Deleted 1 stray file(s), 7 bytes");
    }

    [Fact]
    public async Task AFailureOfTheSweepIsLoggedAndStartupGoesOn()
    {
        var (harness, root, db) = await StartWithADocumentAsync();
        await using var _ = harness;
        var stray = HashFile(root, Hex64);
        var logs = new RecordingLoggerFactory();
        // The catalogue is read once a hash-named file is found, and a context that is gone cannot be read.
        await db.DisposeAsync();

        await Bootstrapper.SweepStrayBlobBytesAsync(db, harness.Settings.Value, logs.CreateLogger("startup"));

        logs.Levels.ShouldBe([LogLevel.Warning]);
        logs.Exceptions.ShouldHaveSingleItem();
        File.Exists(stray).ShouldBeTrue("nothing is deleted on a result that could not be read");
    }
}
