namespace Dexicon.Core.Indexing;

/// <summary>
/// How much one source of patterns may add to a walk: an ignore file tree, or one glob list. A rule counts one rule
/// and the <see cref="GlobMatcher.Weight"/> of its pattern, and the walk fails when either limit is passed, because a
/// walk that kept the rules it had read before the limit would index files the rest of the patterns excluded.
///
/// Every file the walk meets is tested against every rule in force, so the limits bound the cost per file. The
/// figures are in <see cref="IgnoreRuleSet"/>.
/// </summary>
public sealed class RuleBudget(int maxRules, int maxWeight)
{
    public int MaxRules => maxRules;

    public int MaxWeight => maxWeight;

    public int Rules { get; private set; }

    public int Weight { get; private set; }

    /// <summary>The budget for the ignore files of one walk, <c>.git/info/exclude</c> among them.</summary>
    public static RuleBudget ForIgnoreFiles() => new(IgnoreRuleSet.MaxRulesPerSource, IgnoreRuleSet.MaxWeightPerSource);

    /// <summary>The budget for one stored glob list.</summary>
    public static RuleBudget ForList() => new(IgnoreRuleSet.MaxRulesPerList, IgnoreRuleSet.MaxWeightPerList);

    internal bool TryCharge(int weight)
    {
        if (Rules + 1 > maxRules || (long)Weight + weight > maxWeight) return false;

        Rules++;
        Weight += weight;
        return true;
    }
}

/// <summary>
/// What a walk skipped of the files git owns, kept for the caller to write out. The first
/// <see cref="MaxKept"/> descriptions are held and the rest are counted, because a <c>.gitignore</c> of fifty
/// thousand bad lines is fifty thousand log lines otherwise, on every pass. A notice (<see cref="AddNotice"/>) is
/// always held and is not counted against that limit, for a fact the reader must see whatever else was skipped.
/// </summary>
public sealed class WarningSink
{
    public const int MaxKept = 20;

    private readonly List<string> _kept = [];
    private readonly List<string> _notices = [];

    public int Count { get; private set; }

    /// <summary>The descriptions held, in the order they were added, then the notices.</summary>
    public IReadOnlyList<string> Kept => [.. _kept, .. _notices];

    /// <summary>How many descriptions were counted and not kept.</summary>
    public int Omitted => Count - _kept.Count;

    public void Add(string description)
    {
        Count++;
        if (_kept.Count < MaxKept) _kept.Add(description);
    }

    /// <summary>Holds <paramref name="notice"/> whatever number of descriptions were added before it.</summary>
    public void AddNotice(string notice) => _notices.Add(notice);
}
