using Dexicon.Core.Auth;

namespace Dexicon.Mcp;

/// <summary>
/// Which tools a caller is not shown in <c>tools/list</c>. Hiding is for the agent's sake:
/// a tool it can see, it will call, and each definition costs context on every turn. Each
/// tool still checks its own scope when called. Applied by the filter in Program.cs; see
/// docs/decisions.md D-28 and D-36.
/// </summary>
internal static class ToolVisibility
{
    /// <summary>The tools that read the index, each requiring <see cref="Scopes.Search"/>.</summary>
    internal static readonly string[] SearchTools = ["search_index", "list_corpora", "get_context", "index_status"];

    internal static IReadOnlySet<string> HiddenFrom(Principal principal)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        if (!principal.Has(Scopes.Search)) hidden.UnionWith(SearchTools);
        if (!principal.Has(Scopes.Ingest)) hidden.Add("index_refresh");
        if (!principal.Has(Scopes.Configure)) hidden.UnionWith(ConfigureTools.Names);
        if (!principal.Has(Scopes.Propose)) hidden.UnionWith(ProposeTools.Names);
        return hidden;
    }
}
