using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Reading a source's files concurrently, into a stream the indexer consumes one at a
/// time.
///
/// The split exists so that reading — the slow half, which needs nothing shared — can
/// use the whole extraction budget while recording stays on one thread with the
/// catalogue. What has to hold for that to be safe is here: every file arrives exactly
/// once, a file that cannot be read arrives as a failure rather than taking the pass
/// down with it, and a consumer that stops reading does not leave the readers blocked on
/// a bounded channel nobody will drain.
/// </summary>
public sealed class WorkspaceFileReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("reader-").FullName;
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"reader-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    public WorkspaceFileReaderTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<DexiconOptions>>(Options.Create(new DexiconOptions
        {
            Indexing = new IndexingOptions { MaxConcurrentExtractions = 4 },
        }));
        services.AddDbContext<CatalogDbContext>(o => o.UseSqlite($"Data Source={_db}"), ServiceLifetime.Scoped);
        services.AddSingleton<IndexingLimits>();
        services.AddScoped<ExtractedTextCache>();
        services.AddSingleton<WorkspaceFileReader>();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.EnsureCreated();
    }

    /// <summary>Plain text, so the read path needs no extractor and no cached row.</summary>
    private WorkspaceWalker.Candidate Write(string name, string content)
    {
        var full = Path.Combine(_dir, name);
        File.WriteAllText(full, content);
        return new WorkspaceWalker.Candidate(full, name, new FileInfo(full).Length);
    }

    private WorkspaceFileReader Reader() => _services.GetRequiredService<WorkspaceFileReader>();

    [Fact]
    public async Task EveryFileArrivesExactlyOnce()
    {
        // More files than the read-ahead, so the channel fills and the readers have to
        // wait on the consumer at least once.
        var files = Enumerable.Range(0, 25).Select(i => Write($"f{i}.txt", $"contents {i}")).ToList();

        var seen = new List<string>();
        await foreach (var read in Reader().ReadAsync(files))
        {
            read.Error.ShouldBeNull();
            read.Read!.Text.Text.ShouldBe($"contents {read.Candidate.RelativePath[1..^4]}");
            seen.Add(read.Candidate.RelativePath);
        }

        seen.Count.ShouldBe(25);
        seen.Distinct().Count().ShouldBe(25);
    }

    /// <summary>
    /// Reading happens off the indexer's thread, so a failure there has to travel back as
    /// data. Thrown, it would take down the pass instead of costing one file, which is
    /// the rule the indexer is built on.
    /// </summary>
    [Fact]
    public async Task AFileThatCannotBeReadArrivesAsAFailure()
    {
        var good = Write("good.txt", "here");
        var gone = Write("gone.txt", "not for long");
        File.Delete(gone.FullPath);

        var reads = new List<ReadFile>();
        await foreach (var read in Reader().ReadAsync([good, gone])) reads.Add(read);

        reads.Count.ShouldBe(2);
        reads.Single(r => r.Candidate.RelativePath == "good.txt").Error.ShouldBeNull();

        var failed = reads.Single(r => r.Candidate.RelativePath == "gone.txt");
        failed.Error.ShouldBeOfType<FileNotFoundException>();
        failed.Read.ShouldBeNull();
    }

    /// <summary>
    /// The deadlock the bounded channel invites. Readers block once it is full, so a
    /// consumer that stops has to cancel them; without that this never returns, and the
    /// symptom in production is an index job that stops making progress and logs nothing.
    /// </summary>
    [Fact]
    public async Task AbandoningTheEnumerationDoesNotLeaveTheReadersBlocked()
    {
        var files = Enumerable.Range(0, 200).Select(i => Write($"many{i}.txt", $"body {i}")).ToList();

        var reading = Task.Run(async () =>
        {
            await foreach (var _ in Reader().ReadAsync(files)) break;
        });

        // WHICH task finished, not whether the one that did succeeded: a timeout that
        // elapses is a successfully completed task too, so comparing the identity is the
        // only form of this assertion that can fail.
        var finished = await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(30)));

        finished.ShouldBeSameAs(reading,
            "breaking out of the loop must stop the readers, not wait for 200 files");
        await reading;
    }

    [Fact]
    public async Task NoFilesIsNotAnError()
    {
        var reads = 0;
        await foreach (var _ in Reader().ReadAsync([])) reads++;
        reads.ShouldBe(0);
    }

    public void Dispose()
    {
        _services.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        try { File.Delete(_db); } catch (IOException) { }
    }
}
