using System.Security.Cryptography;
using HeuristicLab.Optimization;

namespace HeuristicLab.Next.Runtime;

/// <summary>Distribution of one numeric result over repeated runs.</summary>
public sealed record Statistics(int Count, double Mean, double StdDev, double Min, double Median, double Max) {
  public static Statistics? Of(IEnumerable<double> values) {
    var v = values.Where(double.IsFinite).Order().ToArray();
    if (v.Length == 0) return null;
    double mean = v.Average();
    double sd = v.Length > 1 ? Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Length - 1)) : 0;
    double median = v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    return new Statistics(v.Length, mean, sd, v[0], median, v[^1]);
  }
}

public sealed record BatchReport(
  int BaseSeed, int Repetitions, IReadOnlyList<RunReport> Reports,
  IReadOnlyDictionary<string, Statistics> Summary) {
  public int Completed => Reports.Count(r => r.Outcome == RunOutcome.Completed);
}

/// <summary>
/// Repeats a run with seeds baseSeed, baseSeed+1, ... on fresh copies of the optimizer. Every
/// repetition has an explicit seed, so a batch is reproducible on the same runtime/platform.
/// createOptimizer is called under a lock (it need not be thread-safe); cloning a loaded
/// template (see Copies) is the usual choice.
/// </summary>
public static class BatchRunner {
  /// <summary>Factory that deep-clones the template, as HeuristicLab's BatchRun does.</summary>
  public static Func<IOptimizer> Copies(IOptimizer template) => () => (IOptimizer)template.Clone();

  public static async Task<BatchReport> RepeatAsync(
      Func<IOptimizer> createOptimizer, int repetitions, int? baseSeed = null, int parallelism = 1,
      RunOptions? options = null, string? inputFile = null,
      Action<int, RunReport>? onRunCompleted = null, CancellationToken cancellationToken = default) {
    if (repetitions < 1) throw new ArgumentOutOfRangeException(nameof(repetitions));
    if (parallelism < 1) throw new ArgumentOutOfRangeException(nameof(parallelism));
    int seed0 = baseSeed ?? RandomNumberGenerator.GetInt32(0, int.MaxValue - repetitions);
    var reports = new RunReport[repetitions];
    var factoryLock = new object();

    await Parallel.ForEachAsync(Enumerable.Range(0, repetitions),
      new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = CancellationToken.None },
      async (i, _) => {
        if (cancellationToken.IsCancellationRequested) return;
        var runOptions = (options ?? new RunOptions()) with { Seed = seed0 + i };
        IOptimizer optimizer;
        lock (factoryLock) optimizer = createOptimizer();
        reports[i] = await OptimizerRunner.RunAsync(optimizer, runOptions, inputFile, null, cancellationToken).ConfigureAwait(false);
        onRunCompleted?.Invoke(i, reports[i]);
      }).ConfigureAwait(false);

    var done = reports.Where(r => r != null).ToList();
    return new BatchReport(seed0, repetitions, done, Summarize(done));
  }

  /// <summary>Statistics of every numeric result over the completed runs.</summary>
  public static IReadOnlyDictionary<string, Statistics> Summarize(IEnumerable<RunReport> reports) {
    var runs = reports.Where(r => r.Outcome == RunOutcome.Completed).SelectMany(r => r.Runs).ToList();
    return runs.SelectMany(r => r.Results)
      .Where(r => r.Value.Value is double or int or long)
      .GroupBy(r => r.Key, StringComparer.Ordinal)
      .Select(g => (g.Key, Stats: Statistics.Of(g.Select(r => Convert.ToDouble(r.Value.Value)))))
      .Where(x => x.Stats != null)
      .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(x => x.Key, x => x.Stats!);
  }
}
