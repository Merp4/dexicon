using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dexicon.Core.Indexing;

/// <summary>
/// Exclusive use of one corpus, taken atomically and held by renewal.
///
/// Indexing and discovery run on separate lanes (D-32), so both can want the same corpus
/// at once. Reading <see cref="Corpus.State"/> to decide cannot exclude them: it is set
/// inside the indexer once a job is already running, so a sweep that reads it and then
/// starts is overtaken by a job that starts in the gap. Taking the lease is a single
/// conditional UPDATE and its row count is the answer, so there is no gap to be overtaken
/// in.
///
/// The expiry is renewed while the holder works rather than set to a guess at how long the
/// work will take. That distinction matters here: indexing a library of this size runs for
/// hours, and an expiry chosen for how long a sweep takes would release the lease under a
/// running index job, which is the failure the lease exists to prevent. Renewal means no
/// value has to predict a duration, while a holder that dies stops renewing and its lease
/// falls in after <see cref="Lease"/> rather than never.
/// </summary>
public sealed class CorpusLeases
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CorpusLeases> _log;

    /// <param name="lease">
    /// How long a hold survives without renewal: the reclaim time for a holder that died,
    /// and not a prediction of how long any work takes. Two minutes by default.
    /// </param>
    /// <param name="renew">
    /// Comfortably inside <paramref name="lease"/>, so one missed renewal is survivable.
    /// </param>
    public CorpusLeases(
        IServiceScopeFactory scopes, ILogger<CorpusLeases> log,
        TimeSpan? lease = null, TimeSpan? renew = null)
    {
        _scopes = scopes;
        _log = log;
        Lease = lease ?? TimeSpan.FromMinutes(2);
        Renew = renew ?? TimeSpan.FromSeconds(30);
    }

    public TimeSpan Lease { get; }
    public TimeSpan Renew { get; }

    /// <summary>
    /// Take the corpus, or return null because someone else holds it. The handle renews
    /// in the background and releases on dispose.
    /// </summary>
    public async Task<Hold?> TryAcquireAsync(string corpusId, string holder, CancellationToken ct)
    {
        if (!await ClaimAsync(corpusId, holder, ct)) return null;

        _log.LogDebug("Corpus {Corpus} held by {Holder}", corpusId, holder);
        return new Hold(this, corpusId, holder, _log, Renew);
    }

    /// <summary>
    /// One conditional update. Free means never held, or held by a holder that stopped
    /// renewing; either way the row count decides, not a prior read.
    /// </summary>
    private async Task<bool> ClaimAsync(string corpusId, string holder, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var now = DateTime.UtcNow;
        var rows = await db.Corpora
            .Where(c => c.Id == corpusId
                        && (c.HeldBy == null || c.HeldUntilUtc == null || c.HeldUntilUtc < now))
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.HeldBy, holder)
                .SetProperty(c => c.HeldUntilUtc, now + Lease), ct);

        return rows == 1;
    }

    /// <summary>
    /// Extend, but only while the lease is still ours AND has not already lapsed.
    ///
    /// Matching the holder alone is not enough. A holder whose renewal is late is
    /// indistinguishable from a dead one, and the expiry exists so the corpus falls free;
    /// letting it renew afterwards would resurrect a claim that had already lapsed and
    /// could hold the corpus indefinitely, which is the recovery this is supposed to
    /// provide failing quietly.
    /// </summary>
    private async Task<bool> RenewAsync(string corpusId, string holder, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var now = DateTime.UtcNow;
        var rows = await db.Corpora
            .Where(c => c.Id == corpusId && c.HeldBy == holder && c.HeldUntilUtc > now)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.HeldUntilUtc, now + Lease), ct);

        return rows == 1;
    }

    /// <summary>Only our own hold: releasing someone else's would be worse than leaking ours.</summary>
    private async Task ReleaseAsync(string corpusId, string holder)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        await db.Corpora
            .Where(c => c.Id == corpusId && c.HeldBy == holder)
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.HeldBy, (string?)null)
                .SetProperty(c => c.HeldUntilUtc, (DateTime?)null), CancellationToken.None);
    }

    /// <summary>
    /// A held corpus. Renews until disposed, and stops claiming to hold anything the
    /// moment a renewal finds the lease is no longer ours.
    /// </summary>
    public sealed class Hold : IAsyncDisposable
    {
        private readonly CorpusLeases _owner;
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationTokenSource _lost = new();
        private readonly Task _renewals;
        private readonly ILogger _log;

        private readonly TimeSpan _renew;

        internal Hold(CorpusLeases owner, string corpusId, string holder, ILogger log, TimeSpan renew)
        {
            _owner = owner;
            CorpusId = corpusId;
            Holder = holder;
            _log = log;
            _renew = renew;
            _renewals = Task.Run(() => RenewLoopAsync(_stop.Token));
        }

        public string CorpusId { get; }
        public string Holder { get; }

        /// <summary>
        /// Fires when a renewal finds the lease is no longer ours.
        ///
        /// A token rather than a flag because a flag is something a caller can forget to
        /// read, and the consequence of not reading it is two passes writing the same rows
        /// with nothing to stop either. Both callers link it into the token their work
        /// already honours, so losing the lease ends the work instead of being recorded
        /// next to it.
        /// </summary>
        public CancellationToken Lost => _lost.Token;

        private bool IsLost => _lost.IsCancellationRequested;

        private async Task RenewLoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(_renew);
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    if (await _owner.RenewAsync(CorpusId, Holder, ct)) continue;

                    // Someone else holds it, which means this holder was slow enough to
                    // look dead. Cancelling stops the work rather than letting it carry on
                    // writing beside whoever took the corpus.
                    _log.LogWarning(
                        "Corpus {Corpus} lease lost by {Holder}; it was not renewed in time",
                        CorpusId, Holder);
                    await _lost.CancelAsync();
                    return;
                }
            }
            catch (OperationCanceledException) { /* disposed, which is the normal exit */ }
            catch (Exception ex)
            {
                // The renewal loop failing must not take the work down with it. The lease
                // lapses instead, which is the safe direction.
                _log.LogWarning(ex, "Renewing the lease on {Corpus} failed", CorpusId);
            }
        }

        private bool _disposed;

        /// <summary>
        /// Idempotent, because a hold is routinely both held by `await using` and released
        /// explicitly when the caller wants the corpus free before its scope ends. The
        /// second call used to throw on the already-disposed token source.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            await _stop.CancelAsync();
            try { await _renewals; } catch (OperationCanceledException) { /* expected */ }

            // Only our own hold, and only if we still have it: releasing after losing it
            // would clear the row for whoever took it.
            if (!IsLost) await _owner.ReleaseAsync(CorpusId, Holder);

            _stop.Dispose();
            _lost.Dispose();
        }
    }
}
