// Dedupes correction pairs into confidence-scored rules (#43).
namespace Vtk.Core.Learn;

/// <summary>A deduped wrong→right correction rule with its evidence strength.</summary>
public sealed record Rule(string Base, string Wrong, string Right, ErrorType Error, int Occurrences, double Confidence);

public static class RuleMiner
{
    /// <summary>
    /// Groups pairs by (wrong, right), counts occurrences, and scores
    /// confidence as <c>1 − 0.5^occurrences</c> (0.50, 0.75, 0.875, … —
    /// monotone in evidence, asymptotic to 1). Rules below either threshold
    /// are dropped. Sorted by occurrences desc, then confidence desc, then
    /// wrong asc for a deterministic rules file.
    /// </summary>
    public static List<Rule> Mine(IEnumerable<CorrectionPair> pairs, double minConfidence, int minOccurrences)
    {
        var agg = new Dictionary<(string Wrong, string Right), (CorrectionPair First, int Count)>();
        foreach (var p in pairs)
        {
            var key = (p.Wrong, p.Right);
            agg[key] = agg.TryGetValue(key, out var cur) ? (cur.First, cur.Count + 1) : (p, 1);
        }
        var rules = new List<Rule>();
        foreach (var ((wrong, right), (first, count)) in agg)
        {
            var confidence = 1 - Math.Pow(0.5, count);
            if (count < minOccurrences || confidence < minConfidence) continue;
            rules.Add(new Rule(first.Base, wrong, right, first.Error, count, confidence));
        }
        rules.Sort((a, b) =>
        {
            if (a.Occurrences != b.Occurrences) return b.Occurrences.CompareTo(a.Occurrences);
            if (a.Confidence != b.Confidence) return b.Confidence.CompareTo(a.Confidence);
            return string.CompareOrdinal(a.Wrong, b.Wrong);
        });
        return rules;
    }
}
