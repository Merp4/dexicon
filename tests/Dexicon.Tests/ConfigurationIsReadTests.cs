using System.Reflection;
using System.Text.RegularExpressions;
using Dexicon.Core.Configuration;

namespace Dexicon.Tests;

/// <summary>
/// "A value nobody reads is a feature that does not exist."
///
/// Dexicon shipped exactly that defect once: <c>EmbeddingOptions.MaxConcurrency</c> was
/// declared, documented in .env.example and passed by docker-compose, while nothing in
/// the codebase read it — so raising it changed nothing, silently. This test catches
/// the whole class rather than that one instance.
/// </summary>
public sealed class ConfigurationIsReadTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LICENSE"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static IEnumerable<Type> OptionTypes() =>
        typeof(DexiconOptions).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(DexiconOptions).Namespace && t.Name.EndsWith("Options", StringComparison.Ordinal));

    [Fact]
    public void EveryOptionPropertyIsReadSomewhere()
    {
        var root = RepoRoot();
        var sources = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            // Configuration sources ARE scanned: a computed property such as
            // StorageOptions.CatalogPath is a legitimate reader of CatalogFileName.
            // A declaration never contains ".PropertyName", so nothing self-satisfies.
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();

        var unread = new List<string>();

        foreach (var type in OptionTypes())
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                // A nested options object is "read" by virtue of its own properties.
                if (OptionTypes().Contains(prop.PropertyType)) continue;

                // Count whole-word occurrences across the tree. The declaration itself
                // contributes exactly one, so anything above that is a genuine read.
                // Matching ".Name" alone was too strict: a computed property in the
                // same class refers to its sibling bare, as
                // `Path.Combine(DataPath, CatalogFileName)`.
                var word = new Regex($@"\b{Regex.Escape(prop.Name)}\b");
                var occurrences = sources.Sum(s => word.Count(s));
                if (occurrences <= 1) unread.Add($"{type.Name}.{prop.Name}");
            }
        }

        unread.ShouldBeEmpty(
            "these settings are configurable but nothing reads them, so changing them does nothing: "
            + string.Join(", ", unread));
    }

    [Fact]
    public void EveryDocumentedEnvironmentVariableBindsToARealOption()
    {
        // The reverse direction: .env.example must not promise a knob that the options
        // tree has no home for.
        var root = RepoRoot();
        var example = File.ReadAllText(Path.Combine(root, ".env.example"));
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));

        // DEXICON__SECTION__KEY as written in compose.
        var bound = Regex.Matches(compose, @"DEXICON__([A-Z]+)__([A-Z]+)\s*:")
            .Select(m => (Section: m.Groups[1].Value, Key: m.Groups[2].Value))
            .Distinct()
            .ToList();

        bound.ShouldNotBeEmpty();

        var missing = new List<string>();
        foreach (var (section, key) in bound)
        {
            var sectionProp = typeof(DexiconOptions)
                .GetProperties()
                .FirstOrDefault(p => string.Equals(p.Name, section, StringComparison.OrdinalIgnoreCase));

            if (sectionProp is null) { missing.Add($"DEXICON__{section}__ (no such section)"); continue; }

            var keyProp = sectionProp.PropertyType
                .GetProperties()
                .FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));

            if (keyProp is null) missing.Add($"DEXICON__{section}__{key}");
        }

        missing.ShouldBeEmpty($"compose sets variables that bind to nothing: {string.Join(", ", missing)}");
        example.ShouldNotBeEmpty();
    }
}
