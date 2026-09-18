using Dexicon.Core.Embedding;
using Microsoft.Extensions.Logging;

namespace Dexicon.Core.Indexing;

/// <summary>
/// How many characters of THIS file make one token, asked of the model itself.
///
/// The chunk budget is set in tokens and enforced in characters, and the ratio between
/// them is a property of the text, not of the model alone. Measured across a 96-book
/// library it runs from 2.93 characters a token in the code-heavy chapters of a
/// programming book to 5.94 in plain prose: a factor of two.
///
/// One ratio for the whole library cannot serve both ends of that. The model's own
/// measured 3.80 is an average over prose, code and JSON, so it sits above the dense end:
/// at a 7,782-character budget the dense files overflowed the model's 2,048 tokens and
/// were embedded with their tails missing, 220 times in one index run. Sizing the whole
/// library for the dense end instead would mean a 5,997-character budget, and a run at
/// 5,460 characters retrieved measurably worse than one at 7,480, so that trades a loss
/// across every chunk to protect about 1.7% of them.
///
/// Measuring per file is what separates the two. A file denser than the model's average
/// gets a budget scaled to its own density; a file at or above the average is left alone,
/// because the cost of this is only worth paying where it buys something.
///
/// Three windows rather than one: density varies within a book, and a programming book
/// opens with prose. The densest window wins, because the budget has to hold for the
/// worst chunk in the file, not the average one.
/// </summary>
public static class TextDensity
{
    private const int WindowChars = 3_000;
    private const int Windows = 3;

    /// <summary>
    /// The lowest characters-per-token found in this text, or null when it could not be
    /// measured: too short to sample, or a provider that reports no token counts.
    ///
    /// Null means "no opinion" and leaves the caller on the model's ratio, which is the
    /// behaviour before anything was measured per file.
    /// </summary>
    public static async Task<double?> MeasureAsync(
        IEmbeddingService embedder, EmbeddingTarget target, string text,
        ILogger? log = null, CancellationToken ct = default)
    {
        // Too short to be worth sampling, and too short to produce an over-long chunk.
        if (text.Length < WindowChars * 2) return null;

        double? densest = null;

        foreach (var window in Sample(text))
        {
            int? tokens;
            try
            {
                tokens = await embedder.CountTokensAsync(target, window, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A measurement that cannot be taken is absent, not fatal. Indexing a file
                // is the job; knowing its density exactly is an optimisation on top.
                log?.LogDebug(ex, "Could not measure token density with {Target}", target);
                return null;
            }

            // A count at or above the window's own length in tokens means the provider is
            // not counting what we think it is. A zero would divide to infinity.
            if (tokens is null or <= 0) return null;

            var ratio = (double)window.Length / tokens.Value;
            densest = densest is null ? ratio : Math.Min(densest.Value, ratio);
        }

        return densest;
    }

    /// <summary>
    /// Windows spread through the text rather than taken from its head. A book opens with
    /// front matter and a contents page, which tokenize nothing like its middle.
    /// </summary>
    private static IEnumerable<string> Sample(string text)
    {
        for (var i = 0; i < Windows; i++)
        {
            // Evenly spaced through the body, avoiding both ends.
            var at = (int)(text.Length * (i + 1.0) / (Windows + 1.0));
            var start = Math.Clamp(at - WindowChars / 2, 0, Math.Max(text.Length - WindowChars, 0));
            yield return text.Substring(start, Math.Min(WindowChars, text.Length - start));
        }
    }
}
