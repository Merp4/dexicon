using System.ComponentModel;
using System.Text;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Infrastructure;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Dexicon.Mcp;

/// <summary>
/// Asking for a removal, for a key holding <c>propose</c>. Nothing is removed by the call: it records
/// the request, and a person approves or rejects it in the UI, which runs the same code the admin's
/// delete runs. The key is never shown a way to remove anything itself, and the outcome comes back
/// through <c>index_status</c>.
///
/// A key without the scope is not shown this tool (Program.cs), and it checks the scope itself as
/// well. See docs/decisions.md D-39.
/// </summary>
[McpServerToolType]
public sealed class ProposeTools
{
    /// <summary>The tools a key without <see cref="Scopes.Propose"/> is not shown.</summary>
    internal static readonly string[] Names = ["propose_removal"];

    [McpServerTool(Name = "propose_removal")]
    [Description("Ask for a source, chunk set, document or corpus to be removed. Nothing is removed by this call: a person approves or rejects it in the Dexicon UI, and index_status shows how it was decided.")]
    public static async Task<string> ProposeRemovalAsync(
        RequestContext rc,
        ScopeResolver scopes,
        ProposalService proposals,
        [Description("What to remove: source, chunk_set, document or corpus.")] string kind,
        [Description("The corpus it is in, as list_corpora names it. For kind corpus, the one to remove.")] string corpus,
        [Description("What to remove from that corpus. A source: its folder as index_status shows it, with files: or history: in front when both read the folder. A chunk set: its name. A document: its path. Leave out for a corpus.")] string? target = null,
        [Description("One line on why, at most 300 characters. The person deciding reads it beside what would go.")] string? reason = null,
        CancellationToken ct = default)
    {
        DexiconTools.Require(rc, Scopes.Propose);
        var principal = rc.RequirePrincipal();

        if (!ProposalKinds.TryParse(kind, out var parsed))
            throw new McpException($"kind is {ProposalKinds.Choices}, not '{DexiconTools.OneLine(kind ?? string.Empty)}'.");

        var found = await ConfigureTools.WritableAsync(scopes, principal, corpus, string.Empty, ct);

        var asked = await proposals.ProposeAsync(principal, found, parsed, target, reason, ct);
        if (asked.Refusal is { } refused) throw new McpException(DexiconTools.OneLine(refused.Detail));

        var p = asked.Value!.Proposal;
        var what = $"remove the {Describe(p.Kind)} {DexiconTools.OneLine(p.TargetLabel)} from corpus '{DexiconTools.OneLine(p.CorpusName)}'";
        var status = principal.Has(Scopes.Search)
            ? $" index_status(corpus: {DexiconTools.Quoted(p.CorpusName)}) shows how it is decided."
            : string.Empty;

        return asked.Value.AlreadyPending
            ? $"Already waiting: proposal {p.Id} to {what}, made {p.CreatedUtc:u}"
              + $"{(p.TokenId == principal.TokenId ? " by this key" : " by another key")}. Nothing new was recorded.{status}"
            : $"Recorded proposal {p.Id} to {what}. It is awaiting approval in the Dexicon UI, and nothing has been removed.{status}";
    }

    /// <summary>"source", "chunk set", "document" or "corpus", as a sentence uses it.</summary>
    internal static string Describe(ProposalKind kind) => ProposalKinds.Name(kind).Replace('_', ' ');

    /// <summary>
    /// What this key has asked for in a corpus and how each was decided, for <c>index_status</c>. Only a
    /// key's own are listed: another key's asks are not its to read. Empty when there are none.
    /// </summary>
    internal static async Task<string> RenderOwnAsync(
        CatalogDbContext db, Principal principal, Corpus corpus, CancellationToken ct)
    {
        var own = (await db.Proposals.AsNoTracking()
                .Where(p => p.TokenId == principal.TokenId && p.CorpusId == corpus.Id)
                .ToListAsync(ct))
            .OrderBy(p => p.Status == ProposalStatus.Pending ? 0 : 1)
            .ThenByDescending(p => p.CreatedUtc)
            .Take(8)
            .ToList();
        if (own.Count == 0) return string.Empty;

        var sb = new StringBuilder("  removals this key has asked for:\n");
        foreach (var p in own)
        {
            sb.Append($"    {p.Id}  {Describe(p.Kind)} {DexiconTools.OneLine(p.TargetLabel)}  ");
            sb.Append(p.Status switch
            {
                ProposalStatus.Pending => $"waiting since {p.CreatedUtc:u}",
                ProposalStatus.Approved => $"approved {p.DecidedUtc:u}: it has been removed",
                ProposalStatus.Rejected => $"rejected {p.DecidedUtc:u}: it stays",
                _ => $"could not be done ({DexiconTools.OneLine(p.Error ?? "no reason recorded")})",
            });
            sb.Append('\n');
        }

        return sb.ToString();
    }
}
