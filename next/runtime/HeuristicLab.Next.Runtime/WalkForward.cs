using HeuristicLab.Optimization;
using HeuristicLab.Problems.DataAnalysis;
using HeuristicLab.Problems.DataAnalysis.Symbolic;

namespace HeuristicLab.Next.Runtime;

/// <summary>
/// Walk-forward windows over time-ordered rows. Rolling: training windows of fixed size move by
/// Step (default: TestSize). Expanding: training always starts at Start and grows. The test window
/// directly follows its training window, so models never see test rows during training.
/// </summary>
public sealed record WalkForwardOptions(int TrainingSize, int TestSize) {
  public int? Step { get; init; }
  public bool Expanding { get; init; }
  /// <summary>First training row (e.g. after a warm-up).</summary>
  public int Start { get; init; }
}

public sealed record WalkForwardFold(int Index, (int Start, int End) Training, (int Start, int End) Test, RunReport Report);

public sealed record WalkForwardReport(IReadOnlyList<WalkForwardFold> Folds, IReadOnlyDictionary<string, Statistics> TestSummary);

public static class WalkForward {
  public static IReadOnlyList<((int Start, int End) Training, (int Start, int End) Test)> Windows(int rows, WalkForwardOptions o) {
    if (o.TrainingSize < 2 || o.TestSize < 1) throw new ArgumentException("Training size must be at least 2 and test size at least 1.");
    int step = o.Step ?? o.TestSize;
    if (step < 1) throw new ArgumentException("Step must be at least 1.");
    var windows = new List<((int, int), (int, int))>();
    for (int trainingEnd = o.Start + o.TrainingSize; trainingEnd + o.TestSize <= rows; trainingEnd += step) {
      int trainingStart = o.Expanding ? o.Start : trainingEnd - o.TrainingSize;
      windows.Add(((trainingStart, trainingEnd), (trainingEnd, trainingEnd + o.TestSize)));
    }
    if (windows.Count == 0) throw new ArgumentException($"{rows} rows are too few for one window of {o.TrainingSize} training + {o.TestSize} test rows.");
    return windows;
  }

  /// <summary>Trains a fresh copy of the algorithm per window and summarizes the test metrics.</summary>
  public static async Task<WalkForwardReport> RunAsync(
      IAlgorithm template, WalkForwardOptions options, int? seed = null, int parallelism = 1, RunOptions? runOptions = null,
      string? inputFile = null, Action<WalkForwardFold>? onFold = null, CancellationToken cancellationToken = default) {
    var problemData = ProblemData(template);
    var windows = Windows(problemData.Dataset.Rows, options);
    var folds = new WalkForwardFold?[windows.Count];
    var cloneLock = new object();
    await Parallel.ForEachAsync(Enumerable.Range(0, windows.Count),
      new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = CancellationToken.None },
      async (i, _) => {
        if (cancellationToken.IsCancellationRequested) return;
        IAlgorithm algorithm;
        lock (cloneLock) algorithm = (IAlgorithm)template.Clone();
        var (training, test) = windows[i];
        SetPartitions(algorithm, training, test);
        var labels = new Dictionary<string, string>(runOptions?.Labels ?? new Dictionary<string, string>()) {
          ["fold"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
          ["training"] = $"{training.Start}..{training.End - 1}",
          ["test"] = $"{test.Start}..{test.End - 1}"
        };
        var options = (runOptions ?? new RunOptions()) with { Seed = seed, Labels = labels };
        var report = await OptimizerRunner.RunAsync(algorithm, options, inputFile, null, cancellationToken);
        folds[i] = new WalkForwardFold(i, training, test, report);
        onFold?.Invoke(folds[i]!);
      });
    var done = folds.Where(f => f != null).Select(f => f!).ToList();
    return new WalkForwardReport(done, TestSummary(done));
  }

  /// <summary>Statistics over folds of every numeric result measured on the test partition.</summary>
  public static IReadOnlyDictionary<string, Statistics> TestSummary(IEnumerable<WalkForwardFold> folds) =>
    BatchRunner.Summarize(folds.Select(f => f.Report))
      .Where(s => s.Key.Contains("(test)", StringComparison.Ordinal))
      .ToDictionary(s => s.Key, s => s.Value);

  /// <summary>Sets training/test partitions, including a symbolic problem's fitness partition.</summary>
  public static void SetPartitions(IAlgorithm algorithm, (int Start, int End) training, (int Start, int End) test) {
    var data = ProblemData(algorithm);
    // shrink before growing so that Start <= End holds at every step
    data.TestPartition.Start = Math.Min(data.TestPartition.Start, test.Start);
    data.TrainingPartition.Start = Math.Min(data.TrainingPartition.Start, training.Start);
    data.TrainingPartition.End = training.End; data.TrainingPartition.Start = training.Start;
    data.TestPartition.End = test.End; data.TestPartition.Start = test.Start;
    if (algorithm.Problem is ISymbolicDataAnalysisProblem symbolic) {
      symbolic.FitnessCalculationPartition.Start = Math.Min(symbolic.FitnessCalculationPartition.Start, training.Start);
      symbolic.FitnessCalculationPartition.End = training.End;
      symbolic.FitnessCalculationPartition.Start = training.Start;
    }
  }

  private static IDataAnalysisProblemData ProblemData(IAlgorithm algorithm) =>
    (algorithm.Problem as IDataAnalysisProblem)?.ProblemData
    ?? throw new ArgumentException("Walk-forward validation needs a data analysis problem (regression, classification, time series, trading).");
}
