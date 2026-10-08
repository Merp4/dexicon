using Dexicon.Core.Auth;
using Dexicon.Infrastructure;

namespace Dexicon.Api;

/// <summary>
/// Deciding the removals agents have asked for. Administration only, and no key can hold it
/// (D-28), so a key that asked cannot also approve. See docs/decisions.md D-39.
/// </summary>
public static class ProposalEndpoints
{
    /// <summary>How many a listing returns. The waiting ones are capped per key; decided ones are only a record.</summary>
    private const int ListMax = 200;

    public static void MapProposalEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/proposals").WithTags("Proposals");

        // Waiting ones by default, oldest first so none is buried. decided=true lists the rest, newest first.
        g.MapGet("/", async (bool? decided, RequestContext rc, ProposalService proposals, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            return Results.Ok(await proposals.ListAsync(decided ?? false, ListMax, ct));
        }).Produces<IReadOnlyList<ProposalView>>();

        g.MapPost("/{id}/approve", async (string id, RequestContext rc, ProposalService proposals, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var approved = await proposals.ApproveAsync(id, ct);
            return approved.Refusal is { } refused ? refused.ToResult() : Results.Ok(approved.Value);
        }).Produces<ProposalView>();

        g.MapPost("/{id}/reject", async (string id, RequestContext rc, ProposalService proposals, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var rejected = await proposals.RejectAsync(id, ct);
            return rejected.Refusal is { } refused ? refused.ToResult() : Results.Ok(rejected.Value);
        }).Produces<ProposalView>();
    }
}
