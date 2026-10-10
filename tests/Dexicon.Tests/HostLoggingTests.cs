using Dexicon.Core.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dexicon.Tests;

/// <summary>
/// The application itself, started the way it is deployed, with its console captured: its logger writes
/// through <see cref="Dexicon.Infrastructure.OneLineLogSink"/>, startup reports an unusable chunk default
/// once, and a failure in `Bootstrapper.InitialiseAsync` or while running is logged through the sink and not printed raw.
/// A failure in `builder.Build()`, which runs before the `try` in Program.cs, is not covered. Reverting
/// <c>Log.Logger = ...</c> or <c>UseSerilog()</c> in <c>Program.cs</c> fails these.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public sealed class HostLoggingTests : IDisposable
{
    private const string ForgedLine = "[10:00:00Z INF] forged";

    private readonly string _data = Path.Combine(Path.GetTempPath(), $"dexicon-host-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _saved = [];
    private readonly TextWriter _console = Console.Out;

    private void Set(string name, string? value)
    {
        if (!_saved.ContainsKey(name)) _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>Environment for a start that reaches nothing outside the machine and keeps its data in a temp folder.</summary>
    private void Isolate()
    {
        Directory.CreateDirectory(_data);
        Set("DEXICON__STORAGE__DATAPATH", _data);
        Set("DEXICON__QDRANT__ENDPOINT", "http://127.0.0.1:1");
        Set("DEXICON__OLLAMA__ENDPOINT", "http://127.0.0.1:1");
        Set("DEXICON__OLLAMA__MAXRETRIES", "0");
        Set("DEXICON__INDEXING__REFRESHMINUTES", "0");
        Set("DEXICON__ADMIN__PASSWORD", "a-test-password-for-this-run");
    }

    public void Dispose()
    {
        Console.SetOut(_console);
        Serilog.Log.CloseAndFlush();
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        // The host's catalogue connection is pooled and keeps catalog.db open after the host is disposed.
        // The pool is keyed on the connection string, so this clears that pool and no other.
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={new StorageOptions { DataPath = _data }.CatalogPath}"));
        DeleteData();
    }

    /// <summary>Retries a few times, and fails the test when the folder is still there, so a leak is not silent.</summary>
    private void DeleteData()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    [Fact]
    public async Task TheApplicationLogsThroughTheSinkAndAnUnusableChunkDefaultIsReportedOnce()
    {
        Isolate();
        Set("DEXICON__INDEXING__CHUNKSIZE", "10");
        var captured = new StringWriter();
        Console.SetOut(captured);

        await using var factory = new WebApplicationFactory<Program>();
        var logs = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Dexicon.Test");
        logs.LogError(new InvalidOperationException($"Unknown corpus 'x\n{ForgedLine}'."), "Refused {Name}", $"a\n{ForgedLine}{TestText.Csi}");

        var lines = TestText.Lines(captured.ToString());
        lines.Where(l => l.Contains("ERR] The chunk settings in the server's configuration", StringComparison.Ordinal))
            .ShouldHaveSingleItem().ShouldContain("DEXICON__INDEXING__CHUNKSIZE=10");
        lines.ShouldNotContain(l => l.StartsWith(ForgedLine, StringComparison.Ordinal));
        lines.ShouldContain(l => l.StartsWith("    " + ForgedLine, StringComparison.Ordinal), "the exception's forged line, indented");
        captured.ToString().ShouldNotContain(TestText.Csi);
        lines.ShouldContain(l => l.Contains("ERR] Refused \"a\\n" + ForgedLine, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailureInStartupIsLoggedThroughTheSinkAndNotPrintedRaw()
    {
        Isolate();
        Set("DEXICON__BOOTSTRAP__TOKEN", $"not-a-token\n{ForgedLine}");
        var captured = new StringWriter();
        var error = new StringWriter();
        var originalError = Console.Error;
        Console.SetOut(captured);
        Console.SetError(error);
        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            try { _ = factory.Server; } catch (Exception) { /* the entry point ended without a host */ }
        }
        finally
        {
            Console.SetError(originalError);
        }

        var lines = TestText.Lines(captured.ToString());
        lines.ShouldContain(l => l.Contains("FTL] Dexicon terminated unexpectedly", StringComparison.Ordinal));
        lines.ShouldNotContain(l => l.StartsWith(ForgedLine, StringComparison.Ordinal));
        error.ToString().ShouldNotContain("Unhandled exception");
    }
}
