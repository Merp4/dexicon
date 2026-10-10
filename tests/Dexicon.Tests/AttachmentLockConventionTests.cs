using System.Text.RegularExpressions;

namespace Dexicon.Tests;

/// <summary>
/// A test class that holds the attachment lock, or holds a lookup open inside code that holds it, makes every
/// other class that attaches or removes wait, and is itself delayed by them. Those classes run in
/// <see cref="AttachmentLockCollection"/>, which runs alone. Nothing but this scan enforces the membership, and
/// it names the classes that are missing from it.
///
/// Classes that only attach, detach or remove, and hold the lock for the milliseconds of one save, are not
/// in it: running them alone would serialise most of the suite.
/// </summary>
public sealed class AttachmentLockConventionTests
{
    private static readonly string[] Holders = ["HoldAttachmentsAsync", "HoldOneLookup", "HoldTheLookup"];

    private static readonly Regex Declaration = new(
        @"^(?<attrs>(\[[^\n]*\]\s*\n)*)(public|internal)\s+(sealed\s+)?class\s+(?<name>\w+)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static string TestsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dexicon.slnx"))) dir = dir.Parent;
        return Path.Combine(dir.ShouldNotBeNull("the repository root").FullName, "tests", "Dexicon.Tests");
    }

    [Fact]
    public void EveryClassThatHoldsTheLockOrAGateInsideItIsInTheCollectionThatRunsAlone()
    {
        var files = Directory.EnumerateFiles(TestsDirectory(), "*.cs").ToList();
        files.Count.ShouldBeGreaterThan(50, "the scan has to have found the test sources");

        var classesChecked = 0;
        var missing = new List<string>();
        foreach (var file in files.Where(f => !Path.GetFileName(f).StartsWith("Hold", StringComparison.Ordinal)
                                              && Path.GetFileName(f) != "AttachmentLockConventionTests.cs"))
        {
            var text = File.ReadAllText(file);
            var found = Declaration.Matches(text).ToList();
            for (var i = 0; i < found.Count; i++)
            {
                var end = i + 1 < found.Count ? found[i + 1].Index : text.Length;
                var body = text[found[i].Index..end];
                if (!body.Contains("[Fact", StringComparison.Ordinal) && !body.Contains("[Theory", StringComparison.Ordinal))
                    continue;

                if (!Holders.Any(h => body.Contains(h, StringComparison.Ordinal))) continue;

                classesChecked++;
                if (!found[i].Groups["attrs"].Value.Contains(nameof(AttachmentLockCollection), StringComparison.Ordinal))
                    missing.Add(found[i].Groups["name"].Value);
            }
        }

        classesChecked.ShouldBeGreaterThan(5, "the scan has to have found the classes it is about");
        missing.ShouldBeEmpty("these classes hold the lock or a gate inside it and need [Collection(nameof(AttachmentLockCollection))]");
    }
}
