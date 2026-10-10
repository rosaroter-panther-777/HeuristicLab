using HeuristicLab.Analysis.Statistics;

namespace HeuristicLab.Next.Runtime.Runs;

public sealed record GroupSummary(string Group, int Count, double Mean, double StdDev, double Min, double Q1, double Median, double Q3, double Max);

/// <param name="MannWhitneyP">Two-sided p-value of the Mann-Whitney U test; AdjustedP: Bonferroni-Holm over all pairs.</param>
public sealed record PairwiseComparison(string A, string B, double MannWhitneyP, double AdjustedP, double TTestP, double CohensD, double HedgesG);

/// <param name="KruskalWallisP">Do the groups differ at all (null with fewer than two groups of two runs)?</param>
public sealed record Comparison(string Value, IReadOnlyList<GroupSummary> Groups, double? KruskalWallisP, IReadOnlyList<PairwiseComparison> Pairs);

/// <summary>
/// Statistics of a value (a result or parameter) per group, with the tests of HeuristicLab's
/// statistical tests view: Kruskal-Wallis over all groups, and per pair Mann-Whitney U (Holm-
/// adjusted), t-test, Cohen's d and Hedges' g.
/// </summary>
public static class RunStatistics {
  public static Comparison Compare(IReadOnlyList<RunGroup> groups, string key) {
    var data = groups.Select(g => (g.Name, Values: g.Rows.Select(r => r.Number(key)).OfType<double>().Where(double.IsFinite).ToArray()))
      .Where(g => g.Values.Length > 0).ToList();
    var summaries = data.Select(g => Summary(g.Name, g.Values)).ToList();
    var testable = data.Where(g => g.Values.Length >= 2).ToList();
    double? kruskal = testable.Count >= 2 ? KruskalWallisTest.Test(testable.Select(g => g.Values).ToArray()) : null;
    var pairs = new List<(string A, string B, double U, double T, double D, double G)>();
    for (int i = 0; i < testable.Count; i++)
      for (int j = i + 1; j < testable.Count; j++) {
        var (a, b) = (testable[i].Values, testable[j].Values);
        pairs.Add((testable[i].Name, testable[j].Name, PairwiseTest.MannWhitneyUTest(a, b), PairwiseTest.TTest(a, b),
          SampleSizeDetermination.CalculateCohensD(a, b), SampleSizeDetermination.CalculateHedgesG(a, b)));
      }
    var adjusted = pairs.Count > 0 ? BonferroniHolm.Calculate(0.05, pairs.Select(p => p.U).ToArray(), out _) : [];
    return new Comparison(key, summaries, kruskal,
      pairs.Select((p, i) => new PairwiseComparison(p.A, p.B, p.U, adjusted[i], p.T, p.D, p.G)).ToList());
  }

  public static GroupSummary Summary(string name, IReadOnlyList<double> values) {
    var sorted = values.Order().ToArray();
    double mean = sorted.Average();
    double sd = sorted.Length > 1 ? Math.Sqrt(sorted.Sum(v => (v - mean) * (v - mean)) / (sorted.Length - 1)) : 0;
    return new GroupSummary(name, sorted.Length, mean, sd, sorted[0], Quantile(sorted, 0.25), Quantile(sorted, 0.5), Quantile(sorted, 0.75), sorted[^1]);
  }

  /// <summary>Linear interpolation between order statistics (as R's default, type 7).</summary>
  public static double Quantile(double[] sorted, double p) {
    if (sorted.Length == 1) return sorted[0];
    double h = (sorted.Length - 1) * p;
    int lo = (int)Math.Floor(h);
    return lo + 1 < sorted.Length ? sorted[lo] + (h - lo) * (sorted[lo + 1] - sorted[lo]) : sorted[lo];
  }
}
