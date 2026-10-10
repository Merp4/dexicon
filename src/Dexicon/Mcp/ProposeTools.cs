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
/// the request, and whoever runs Dexicon approves or rejects it, which runs the same code the admin's
/// delete runs. The key is never shown a way to remove anything itself, and the outcome comes back
/// through <c>removal_status</c>, which needs no other scope and no corpus that still exists, and
/// through <c>index_status</c> for a key that can search.
///
/// A key without the scope is not shown these tools (Program.cs), and each checks the scope itself as
/// well. See docs/decisions.md D-39.
/// </summary>
[McpServerToolType]
public sealed class ProposeTools
{
    /// <summary>The tools a key without <see cref="Scopes.Propose"/> is not shown.</summary>
    internal static readonly string[] Names = ["propose_removal", "removal_status"];

    /// <summary>How many requests are listed for one corpus, and across all of them.</summary>
    private const int InCorpus = 8;
    private const int AcrossCorpora = 20;

    [McpServerTool(Name = "propose_removal")]
    [Description("Ask for a source, chunk set, document or corpus to be removed. Nothing is removed by this call: whoever runs Dexicon approves or rejects it, and removal_status shows how it was decided.")]
    public static async Task<string> ProposeRemovalAsync(
        RequestContext rc,
        ScopeResolver scopes,
        ProposalService proposals,
        [Description("What to remove: source, chunk_set, document or corpus.")] string kind,
        [Description("The corpus it is in, as list_corpora names it. For kind corpus, the one to remove.")] string corpus,
        [Description("One line on why, at most 300 characters. Required: the person deciding reads it beside what would go.")] string reason,
        [Description("What to remove from that corpus. A source: its folder as index_status shows it, with files: or history: in front when both read the folder. A chunk set: its name. A document: its path. Leave out for a corpus.")] string? target = null,
        CancellationToken ct = default)
    {
        DexiconTools.Require(rc, Scopes.Propose);
        var principal = rc.RequirePrincipal();

        if (!ProposalKinds.TryParse(kind, out var parsed))
            throw new McpException($"kind is {ProposalKinds.Choices}, not '{DexiconTools.Echo(kind)}'.");

        var found = await ConfigureTools.WritableAsync(scopes, principal, corpus, string.Empty, ct);

        var asked = await proposals.ProposeAsync(principal, found, parsed, target, reason, ct);
        if (asked.Refusal is { } refused) throw DexiconTools.Refusal(refused.Detail);

        var p = asked.Value!.Proposal;
        var what = $"remove {Phrase(p)}";

        if (!asked.Value.AlreadyPending)
            return $"Recorded proposal {p.Id} to {what}. It is waiting for whoever runs Dexicon to decide it, and nothing has been removed. removal_status shows how it is decided.";

        // removal_status lists a key's own requests, so a request another key made is not in it.
        var own = p.TokenId == principal.TokenId;
        return $"Already waiting: proposal {p.Id} to {what}, made {p.CreatedUtc:u} by {(own ? "this key" : "another key")}. "
            + (own
                ? "Nothing new was recorded. removal_status shows how it is decided."
                : "Nothing new was recorded, and removal_status lists only this key's own requests, so it will not show this one.");
    }

    [McpServerTool(Name = "removal_status")]
    [Description("The removals this key has asked for with propose_removal, and how each was decided: waiting, approved (it has been removed), rejected (it stays) or could not be done. Waiting ones come first. Works after the corpus asked about is gone.")]
    public static async Task<string> RemovalStatusAsync(RequestContext rc, CatalogDbContext db, CancellationToken ct = default)
    {
        DexiconTools.Require(rc, Scopes.Propose);
        var principal = rc.RequirePrincipal();

        var own = await RenderOwnAsync(db, principal, corpusId: null, ct);
        return own.Length > 0 ? own : "This key has asked for no removals.\n";
    }

    /// <summary>"source", "chunk set", "document" or "corpus", as a sentence uses it.</summary>
    internal static string Describe(ProposalKind kind) => ProposalKinds.Name(kind).Replace('_', ' ');

    /// <summary>What a request is for, read after "remove": the corpus itself, or a part of one.</summary>
    private static string Phrase(Proposal p) => p.Kind == ProposalKind.Corpus
        ? $"the corpus '{DexiconTools.OneLine(p.CorpusName)}'"
        : $"the {Describe(p.Kind)} {DexiconTools.OneLine(p.TargetLabel)} from corpus '{DexiconTools.OneLine(p.CorpusName)}'";

    /// <summary>
    /// What this key has asked for and how each was decided, waiting ones first and newest first within
    /// each. For one corpus when <paramref name="corpusId"/> is given, as <c>index_status</c> shows it,
    /// and otherwise across all of them. Only a key's own are listed: another key's asks are not its to
    /// read. Empty when there are none.
    /// </summary>
    internal static async Task<string> RenderOwnAsync(
        CatalogDbContext db, Principal principal, string? corpusId, CancellationToken ct)
    {
        var inCorpus = corpusId is not null;
        var take = inCorpus ? InCorpus : AcrossCorpora;

        var query = db.Proposals.AsNoTracking().Where(p => p.TokenId == principal.TokenId);
        if (inCorpus) query = query.Where(p => p.CorpusId == corpusId);

        // One more than is shown, so the listing can say there are others.
        var own = await query
            .OrderBy(p => p.Status == ProposalStatus.Pending ? 0 : 1)
            .ThenByDescending(p => p.CreatedUtc).ThenByDescending(p => p.Id)
            .Take(take + 1)
            .ToListAsync(ct);
        if (own.Count == 0) return string.Empty;

        var indent = inCorpus ? "  " : string.Empty;
        var sb = new StringBuilder($"{indent}removals this key has asked for:\n");
        foreach (var p in own.Take(take))
        {
            var of = p.Kind == ProposalKind.Corpus
                ? $"corpus {DexiconTools.OneLine(p.CorpusName)}"
                : $"{Describe(p.Kind)} {DexiconTools.OneLine(p.TargetLabel)}{(inCorpus ? string.Empty : $" in {DexiconTools.OneLine(p.CorpusName)}")}";

            sb.Append($"{indent}  {p.Id}  {of}  ");
            sb.Append(p.Status switch
            {
                ProposalStatus.Pending => $"waiting since {p.CreatedUtc:u}",
                ProposalStatus.Approved => $"approved {p.DecidedUtc:u}: it has been removed",
                ProposalStatus.Rejected => $"rejected {p.DecidedUtc:u}: it stays",
                _ => $"could not be done ({DexiconTools.OneLine(p.Error ?? "no reason recorded")})",
            });
            sb.Append('\n');
        }

        if (own.Count > take) sb.Append($"{indent}  (older ones are not listed)\n");

        return sb.ToString();
    }
}
