using System.Text;
using System.Text.Json;
using Dexicon.Core.Indexing;

// args: <src root> <out dir> <mode>. Writes chunks-<size>-<overlap>.jsonl per setting: one JSON object per chunk with the file
// (relative to the root), 1-based first and last line, and the text that is embedded. The file set and limits are those of
// scripts/bench/modelbench.py (TEXT_EXT, 256 KiB, no bin, obj, node_modules, .git), taken in ordinal order of the path.
var root = Path.GetFullPath(args[0]);
var outDir = args[1];
var mode = args.Length > 2 ? args[2] : "language-aware";
Directory.CreateDirectory(outDir);

var textExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md", ".cs", ".csproj", ".json", ".props", ".yml", ".yaml", ".txt" };
var skipDirs = new HashSet<string> { "bin", "obj", "node_modules", ".git" };
var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    .Select(p => (full: p, rel: Path.GetRelativePath(root, p).Replace('\\', '/')))
    .Where(f => textExt.Contains(Path.GetExtension(f.full)) && new FileInfo(f.full).Length <= 256 * 1024
             && !f.rel.Split('/')[..^1].Any(skipDirs.Contains))
    .OrderBy(f => f.rel, StringComparer.Ordinal).ToList();
if (files.Count < 100) throw new InvalidOperationException($"only {files.Count} files under {root}");

var settings = new (int size, int overlap)[] { (256, 32), (512, 64), (1024, 128) };
foreach (var (size, overlap) in settings)
{
    var path = Path.Combine(outDir, $"chunks-{size}-{overlap}.jsonl");
    using var w = new StreamWriter(path, false, new UTF8Encoding(false));
    var n = 0;
    foreach (var (full, rel) in files)
    {
        var text = File.ReadAllText(full);
        foreach (var c in CodeChunker.Chunk(rel, text, size, overlap, mode))
        {
            n++;
            var embed = string.IsNullOrEmpty(c.EmbedText) ? c.Content : c.EmbedText;
            w.WriteLine(JsonSerializer.Serialize(new { file = rel, start = c.StartLine, end = c.EndLine, text = embed }));
        }
    }
    Console.WriteLine($"{mode} {size}/{overlap}: {n} chunks from {files.Count} files -> {path}");
}
