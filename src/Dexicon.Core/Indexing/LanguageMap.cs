namespace Dexicon.Core.Indexing;

/// <summary>
/// Extension → canonical language name, and the member-boundary pattern per language.
///
/// One choice is easy to get backwards: HTML, Razor, Vue and Svelte split on blank
/// lines, not on headings. They are template <i>source</i>, not documents, so splitting
/// an Angular template at its <c>&lt;h1&gt;</c> produces slices that mean nothing.
/// </summary>
public static class LanguageMap
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".csx"] = "csharp",
        [".fs"] = "fsharp",
        [".fsx"] = "fsharp",
        [".vb"] = "vb",
        [".java"] = "java",
        [".kt"] = "kotlin",
        [".kts"] = "kotlin",
        [".scala"] = "scala",
        [".swift"] = "swift",
        [".ts"] = "typescript",
        [".tsx"] = "typescript",
        [".mts"] = "typescript",
        [".cts"] = "typescript",
        [".js"] = "javascript",
        [".jsx"] = "javascript",
        [".mjs"] = "javascript",
        [".cjs"] = "javascript",
        [".py"] = "python",
        [".pyi"] = "python",
        [".rb"] = "ruby",
        [".go"] = "go",
        [".rs"] = "rust",
        [".php"] = "php",
        [".lua"] = "lua",
        [".c"] = "c",
        [".h"] = "c",
        [".cpp"] = "cpp",
        [".cc"] = "cpp",
        [".cxx"] = "cpp",
        [".hpp"] = "cpp",
        [".hh"] = "cpp",
        [".sql"] = "sql",
        [".css"] = "css",
        [".scss"] = "scss",
        [".less"] = "less",
        [".sh"] = "shell",
        [".bash"] = "shell",
        [".zsh"] = "shell",
        [".ps1"] = "powershell",
        [".psm1"] = "powershell",
        [".html"] = "html",
        [".htm"] = "html",
        [".razor"] = "razor",
        [".cshtml"] = "razor",
        [".vue"] = "vue",
        [".svelte"] = "svelte",
        [".md"] = "markdown",
        [".markdown"] = "markdown",
        [".json"] = "json",
        [".jsonc"] = "json",
        [".yml"] = "yaml",
        [".yaml"] = "yaml",
        [".toml"] = "toml",
        [".xml"] = "xml",
        [".csproj"] = "xml",
        [".props"] = "xml",
        [".targets"] = "xml",
        [".txt"] = "text",
        [".log"] = "text",
        [".dockerfile"] = "dockerfile",
    };

    private const string BlankLine = @"(?m)^\s*$";

    private static readonly Dictionary<string, string> Boundaries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["markdown"] = @"(?m)^#{1,6}\s",
        ["csharp"] = @"(?m)^\s*(public|private|protected|internal|static|abstract|sealed|override|virtual|async|record|class|struct|interface|enum)\s",
        ["fsharp"] = @"(?m)^\s*(let|type|module|member|override|abstract)\s",
        ["vb"] = @"(?im)^\s*(Public|Private|Protected|Friend|Shared|Overrides|Sub|Function|Class|Interface|Module)\s",
        ["java"] = @"(?m)^\s*(public|private|protected|static|final|abstract|synchronized|native|record|class|interface|enum)\s",
        ["kotlin"] = @"(?m)^\s*(public|private|protected|internal|override|abstract|fun |class |object |interface |data class )",
        ["scala"] = @"(?m)^\s*(def |class |object |trait |val |var )",
        ["swift"] = @"(?m)^\s*(public|private|internal|fileprivate|open|func |class |struct |protocol |extension |enum )",
        ["typescript"] = @"(?m)^(export )?(default )?(async )?(function |class |const |let |var |interface |type |enum |abstract class )",
        ["javascript"] = @"(?m)^(export )?(default )?(async )?(function |class |const |let |var )",
        ["python"] = @"(?m)^(async def |def |class )",
        ["ruby"] = @"(?m)^\s*(def |class |module )",
        ["go"] = @"(?m)^(func |type |var |const )",
        ["rust"] = @"(?m)^\s*(pub )?(async )?(fn |struct |impl |trait |enum |mod |type )",
        ["php"] = @"(?m)^\s*(public|private|protected|static|abstract|function |class )",
        ["lua"] = @"(?m)^(function |local function )",
        ["c"] = @"(?m)^[\w*]+\s+\w+\s*\(",
        ["cpp"] = @"(?m)^[\w:~*&<>]+\s+[\w:~*]+\s*\(",
        ["sql"] = @"(?im)^(CREATE|ALTER|DROP|SELECT|INSERT|UPDATE|DELETE|TRUNCATE|MERGE|WITH)\b",
        ["css"] = @"(?m)^[.#\[\w@:][^{]*\{",
        ["scss"] = @"(?m)^[.#\[\w@:][^{]*\{",
        ["less"] = @"(?m)^[.#\[\w@:][^{]*\{",
        ["shell"] = @"(?m)^(\w+\s*\(\)\s*\{|function\s+\w+)",
        ["powershell"] = @"(?m)^(function\s+|class\s+|\[CmdletBinding)",

        // Template source, not documents. See the class remark.
        ["html"] = BlankLine,
        ["razor"] = BlankLine,
        ["vue"] = BlankLine,
        ["svelte"] = BlankLine,
    };

    /// <summary>Symbol-extraction patterns. A regex pass, explicitly not a parser.</summary>
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = @"\b(?:class|struct|interface|enum|record)\s+(\w+)|\b(?:public|private|protected|internal)\s+(?:static\s+|async\s+|virtual\s+|override\s+|sealed\s+)*[\w<>\[\],\s]+?\s+(\w+)\s*\(",
        ["typescript"] = @"\b(?:class|interface|type|enum)\s+(\w+)|\b(?:function|const|let)\s+(\w+)",
        ["javascript"] = @"\b(?:class)\s+(\w+)|\b(?:function|const|let)\s+(\w+)",
        ["python"] = @"^\s*(?:async\s+)?(?:def|class)\s+(\w+)",
        ["go"] = @"^\s*(?:func|type)\s+(?:\([^)]*\)\s*)?(\w+)",
        ["rust"] = @"^\s*(?:pub\s+)?(?:async\s+)?(?:fn|struct|enum|trait|impl)\s+(\w+)",
        ["java"] = @"\b(?:class|interface|enum|record)\s+(\w+)|\b(?:public|private|protected)\s+(?:static\s+)?[\w<>\[\],\s]+?\s+(\w+)\s*\(",
    };

    public static string Detect(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        if (name.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase)) return "dockerfile";
        if (name.StartsWith('.') && !Path.HasExtension(name[1..])) return "text";

        var ext = Path.GetExtension(relativePath);
        return ByExtension.TryGetValue(ext, out var lang) ? lang : "text";
    }

    public static string? BoundaryPattern(string language) =>
        Boundaries.TryGetValue(language, out var p) ? p : BlankLine;

    public static string? SymbolPattern(string language) =>
        Symbols.TryGetValue(language, out var p) ? p : null;

    public static string? MediaType(string language) => language switch
    {
        "csharp" => "text/x-csharp",
        "typescript" => "text/x-typescript",
        "javascript" => "text/javascript",
        "python" => "text/x-python",
        "markdown" => "text/markdown",
        "html" => "text/html",
        "json" => "application/json",
        "yaml" => "application/yaml",
        "sql" => "application/sql",
        _ => "text/plain",
    };
}
