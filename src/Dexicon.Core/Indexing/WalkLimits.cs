namespace Dexicon.Core.Indexing;

/// <summary>
/// The rules one walk may still read from ignore files and glob lists. Shared by every call that adds to the walk,
/// because the limit is on what each file in the tree is tested against, and that is the sum
/// (<see cref="IgnoreRuleSet.MaxRulesPerSource"/>).
/// </summary>
public sealed class RuleBudget(int limit)
{
    public int Limit => limit;

    public int Used { get; private set; }

    public bool Exhausted => Used >= limit;

    internal void Take() => Used++;
}

/// <summary>
/// What a walk skipped of the files git owns, kept for the caller to write out. The first
/// <see cref="MaxKept"/> descriptions are held and the rest are counted, because a <c>.gitignore</c> of fifty
/// thousand bad lines is fifty thousand log lines otherwise, on every pass.
/// </summary>
public sealed class WarningSink
{
    public const int MaxKept = 20;

    private readonly List<string> _kept = [];

    public int Count { get; private set; }

    public IReadOnlyList<string> Kept => _kept;

    /// <summary>How many descriptions were counted and not kept.</summary>
    public int Omitted => Count - _kept.Count;

    public void Add(string description)
    {
        Count++;
        if (_kept.Count < MaxKept) _kept.Add(description);
    }
}
