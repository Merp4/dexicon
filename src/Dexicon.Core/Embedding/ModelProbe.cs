using Microsoft.Extensions.Logging;

namespace Dexicon.Core.Embedding;

/// <param name="MaxInputChars">
/// The longest input whose tail still affects the vector, measured in CHARACTERS of
/// ordinary English prose. Null when the model accepted everything probed, which means
/// its limit is beyond the ceiling rather than absent.
///
/// The model counts tokens, not characters, and the ratio depends on the text: dense
/// code, minified output and CJK reach the same token limit in far fewer characters. So
/// this is an upper bound for prose, which is why the recommendation below is two thirds
/// of it rather than all of it.
/// </param>
/// <param name="TruncatesSilently">
/// True when over-long input returns a vector instead of an error. The dangerous case:
/// nothing fails, and the missing text is reported as indexed.
/// </param>
/// <param name="RecommendedChunkChars">
/// A chunk budget with headroom under the measured limit, in characters.
/// </param>
/// <param name="CharsPerToken">
/// How many characters of ordinary text this model makes one token of, MEASURED with the
/// model's own tokenizer rather than assumed.
///
/// The chunker has always used a flat 4. That is a fair average for English prose and
/// wrong in the direction that hurts for dense code, minified output and CJK, which reach
/// the same token limit in far fewer characters, so a "768 token" chunk of minified
/// JavaScript can be two or three times that, and the model truncates it without error.
///
/// Null when the provider does not report token counts, in which case callers keep the
/// estimate rather than inventing a measurement.
/// </param>
public sealed record ModelCapabilities(
    string Provider,
    string Model,
    int Dimensions,
    int? MaxInputChars,
    bool TruncatesSilently,
    int RecommendedChunkChars,
    int RecommendedChunkTokens,
    double? CharsPerToken,
    int EmbedCalls,
    long TookMs,
    string Summary);

/// <summary>
/// Measures what a model will actually accept, without indexing anything.
///
/// The question this answers, "how big can a chunk be before the model drops the end of
/// it?", had no answer in Dexicon, and the cost of guessing was concrete. An EPUB
/// once produced chunks averaging 32,000 characters; every one of them was truncated by
/// the model, roughly 95% of the book was in no index anywhere, and the corpus, the job
/// and the file all reported success. Nothing in the system could detect it, because a
/// truncating model returns a perfectly good vector for the part it read.
///
/// The trick is that truncation is EMPIRICALLY VISIBLE even though it is silent: embed a
/// text, then embed the same text with distinctive content appended. If the tail was read,
/// the vector moves. If the vector is unchanged, the tail was discarded. Binary search on
/// that gives the real limit, in about a dozen short calls and no documentation to trust.
/// </summary>
public sealed class ModelProbe(IEmbeddingService embeddings, ILogger<ModelProbe> log)
{
    /// <summary>Beyond this we stop asking. Far past any current embedding model's window.</summary>
    private const int CeilingChars = 262_144;

    /// <summary>Stop narrowing once the bracket is this tight; further precision is noise.</summary>
    private const int ToleranceChars = 256;

    /// <summary>
    /// Vectors this similar are the same answer. Not 1.0: providers vary in the last
    /// decimal place between identical calls, and an exact-equality test would report
    /// every model as non-truncating.
    /// </summary>
    private const double IdenticalEnough = 0.9999;

    public async Task<ModelCapabilities> RunAsync(EmbeddingTarget target, CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var calls = 0;

        async Task<float[]> Embed(string text)
        {
            calls++;
            // Raw, deliberately: this measures what the MODEL does with a given number of
            // characters. A task template would add characters of its own and shift every
            // measurement by the length of a prefix.
            return (await embeddings.EmbedAsync(target, EmbedPurpose.Raw, [text], ct))[0];
        }

        var dimensions = (await Embed("dimension probe")).Length;

        // Measured before the bisection, so both exits report it and neither has to
        // remember to.
        var charsPerToken = await MeasureCharsPerTokenAsync(target, ct);

        // Does the tail of a long input reach the model at all? If it does at the ceiling,
        // there is no limit worth reporting and no search to run.
        var (reachesTail, _) = await TailIsRead(Embed, CeilingChars);
        if (reachesTail)
        {
            var recommendedChars = CeilingChars / 2;
            // No ceiling found, so there is no context to count: the estimate stands.
            return Done(target, dimensions, null, false, recommendedChars, calls, started, null,
                $"Accepted {CeilingChars:N0} characters with the end still affecting the vector. " +
                "No practical limit found; chunk size is a retrieval choice here, not a constraint.",
                charsPerToken);
        }

        // It truncated somewhere. Find where, by bisection on "is the tail still read".
        var low = 0;                 // known read in full
        var high = CeilingChars;     // known truncated

        while (high - low > ToleranceChars)
        {
            var mid = low + (high - low) / 2;
            var (read, _) = await TailIsRead(Embed, mid);
            if (read) low = mid; else high = mid;
        }

        // Does it at least SAY it truncated? A model that errors is far safer than one
        // that answers, because the failure is visible at the point it happens.
        var errors = await ErrorsOnOverlongInput(target, ct);
        calls++;

        // Two thirds of the measured limit. The measurement is in characters and the
        // model counts tokens, and the ratio varies with the text, since code and CJK are
        // denser than English prose, so the headroom absorbs a bad estimate rather
        // than pretending the number is exact.
        var budget = low * 2 / 3;

        // The same boundary, counted in the unit the model actually enforces.
        //
        // `low` is the ceiling in characters OF THE FILLER, which is four repeated words
        // and so tokenizes about as well as text ever does. Converting it with a ratio
        // measured on other text applies a density correction twice in opposite
        // directions, and the headroom above cancels out: on embeddinggemma that reported
        // 2,065 tokens against a 2,048-token context, and about 4% of real embeds were
        // clamped by a recommendation that was supposed to have a third to spare.
        //
        // A chunk budget is in tokens and the limit is in tokens, so the conversion has no
        // business being in the middle of it.
        var contextTokens = await embeddings.CountTokensAsync(target, Filler(low), ct);
        calls++;

        log.LogInformation(
            "{Target}: accepts ~{Limit:N0} chars, {Behaviour} beyond it, recommending {Budget:N0}",
            target, low, errors ? "errors" : "truncates silently", budget);

        return Done(target, dimensions, low, !errors, budget, calls, started, contextTokens,
            errors
                ? $"Accepts about {low:N0} characters of prose and rejects more, which is the safe " +
                  "behaviour: an over-long chunk fails rather than being shortened without notice. " +
                  Density
                : $"Accepts about {low:N0} characters of prose and truncates beyond that without " +
                  "raising an error. It returns a vector for the part it read, so an over-long chunk is indexed as its opening " +
                  $"and the rest is nowhere. Keep the chunk budget under the recommendation. {Density}",
            charsPerToken);
    }

    /// <summary>
    /// Whether the END of an input of this length reaches the model, tested by changing
    /// only the end and watching whether the vector moves.
    /// </summary>
    private static async Task<(bool Read, double Similarity)> TailIsRead(
        Func<string, Task<float[]>> embed, int length)
    {
        // Filler that is uniform, so the only difference between the two inputs is the
        // marker at the end. Repeated words rather than one long token, because some
        // tokenizers collapse runs of an identical character.
        var filler = Filler(length);

        try
        {
            var withoutMarker = await embed(filler);
            var withMarker = await embed(filler[..^Marker.Length] + Marker);

            var similarity = CosineSimilarity(withoutMarker, withMarker);
            return (similarity < IdenticalEnough, similarity);
        }
        catch (EmbeddingUnavailableException)
        {
            // A model that REJECTS over-long input rather than truncating it. Refusing is
            // the safer behaviour and it is still a limit, so it answers the same question
            // as "this length did not work", and the bisection carries on. Letting it
            // escape meant the probe crashed on the models that behave best.
            return (false, 0);
        }
    }

    private const string Density =
        "Measured with English prose. Denser text (code, minified output or CJK) reaches the same " +
        "token limit in fewer characters, which is what the recommendation's headroom is for.";

    /// <summary>Distinctive enough that its presence must move a vector that can see it.</summary>
    private const string Marker = " zqxjk wombat telemetry aubergine ";

    private static string Filler(int length)
    {
        const string word = "alpha beta gamma delta ";
        var text = new System.Text.StringBuilder(length + word.Length);
        while (text.Length < length) text.Append(word);
        return text.ToString(0, length);
    }

    private async Task<bool> ErrorsOnOverlongInput(EmbeddingTarget target, CancellationToken ct)
    {
        try
        {
            await embeddings.EmbedAsync(target, EmbedPurpose.Raw, [Filler(CeilingChars)], ct);
            return false;
        }
        catch (EmbeddingUnavailableException)
        {
            return true;
        }
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        var magnitude = Math.Sqrt(na) * Math.Sqrt(nb);
        return magnitude == 0 ? 0 : dot / magnitude;
    }

    private static ModelCapabilities Done(
        EmbeddingTarget target, int dimensions, int? limit, bool truncates, int budgetChars,
        int calls, System.Diagnostics.Stopwatch started, int? contextTokens, string summary,
        double? charsPerToken = null) =>
        new(target.Provider, target.Model, dimensions, limit, truncates,
            budgetChars,
            // Two thirds of the context, in tokens, when the context could be counted. The
            // same headroom as the character budget and for the same reason, but arrived at
            // without a trip through a ratio measured on different text.
            //
            // Falling back to chars over a ratio keeps a number for providers that report
            // no token counts, where an estimate is all there is.
            contextTokens is { } ctx
                ? ctx * 2 / 3
                : (int)(budgetChars / (charsPerToken ?? Indexing.CodeChunker.CharsPerToken)),
            charsPerToken,
            calls, started.ElapsedMilliseconds, summary);

    /// <summary>
    /// Characters per token, measured with the model's own tokenizer.
    /// </summary>
    /// <remarks>
    /// Three samples, because one number cannot describe every kind of text and an average
    /// over prose alone is the flattering case. Prose is roughly four characters a token;
    /// dense code is nearer three; CJK can be one or less. Averaging them gives a ratio
    /// that is wrong for each and much less wrong than 4 for a mixed corpus.
    ///
    /// Null when the provider reports no token counts. A caller that cannot measure keeps
    /// the estimate rather than inventing a measurement.
    /// </remarks>
    private async Task<double?> MeasureCharsPerTokenAsync(EmbeddingTarget target, CancellationToken ct)
    {
        string[] samples =
        [
            // Ordinary English prose.
            "The indexer reads a folder, extracts text from each file it understands, and "
            + "splits that text into chunks small enough for the embedding model to read "
            + "in one go. Nothing leaves the machine unless a hosted provider is chosen.",

            // Dense code, which tokenizes far worse than prose.
            "public async Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingTarget target, "
            + "EmbedPurpose purpose, IReadOnlyList<string> inputs, CancellationToken ct = "
            + "default) { if (inputs.Count == 0) return []; var gen = factory.GeneratorFor(target); }",

            // Punctuation-heavy structured text.
            "{\"query\":{\"fusion\":\"rrf\"},\"filter\":{\"must\":[{\"key\":\"corpus_id\","
            + "\"match\":{\"any\":[\"01JD…\",\"01JE…\"]}}]},\"limit\":40,\"with_payload\":true}",
        ];

        double totalChars = 0, totalTokens = 0;

        foreach (var sample in samples)
        {
            var tokens = await embeddings.CountTokensAsync(target, sample, ct);
            if (tokens is null or 0) return null;   // the provider does not say; do not guess

            totalChars += sample.Length;
            totalTokens += tokens.Value;
        }

        return Math.Round(totalChars / totalTokens, 2);
    }
}
