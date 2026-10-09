using System.Globalization;
using HeuristicLab.Core;
using HeuristicLab.Optimization;
using HeuristicLab.Problems.DataAnalysis;
using HeuristicLab.Problems.DataAnalysis.Symbolic;
using HeuristicLab.Problems.TravelingSalesman;
using TradingSolution = HeuristicLab.Problems.DataAnalysis.Trading.ISolution;
using TradingProfit = HeuristicLab.Problems.DataAnalysis.Trading.OnlineProfitCalculator;

namespace HeuristicLab.Next.Runtime;

public enum SolutionKind { Regression, Classification, TimeSeries, Trading, Route }

/// <summary>
/// What an optimizer found, ready to display: model text, metrics, and per-row series.
/// Rows: Actual/Predicted cover all dataset rows; Training/Test give the partitions.
/// Trading: Predicted holds the position signals (-1, 0, 1); TrainingEquity/TestEquity are the
/// cumulative profits per partition, computed with HeuristicLab's own profit calculator.
/// Route: the tour's coordinates in visiting order, closed (first point repeated at the end).
/// </summary>
public sealed record SolutionView(
  string Name, SolutionKind Kind, string Title, string? Model, IReadOnlyDictionary<string, double> Metrics,
  string? TargetName = null, IReadOnlyList<double>? Actual = null, IReadOnlyList<double>? Predicted = null,
  (int Start, int End) Training = default, (int Start, int End) Test = default,
  IReadOnlyList<double>? TrainingEquity = null, IReadOnlyList<double>? TestEquity = null,
  IReadOnlyList<(double X, double Y)>? Route = null);

public static class Solutions {
  /// <summary>Solutions among an algorithm's current results (or, for other optimizers, its last run).</summary>
  public static IReadOnlyList<SolutionView> Find(IOptimizer optimizer) {
    IEnumerable<(string, IItem?)> results = optimizer is IAlgorithm algorithm
      ? algorithm.Results.Select(r => (r.Name, (IItem?)r.Value))
      : optimizer.Runs.LastOrDefault()?.Results.Select(r => (r.Key, (IItem?)r.Value)) ?? [];
    return Find(results);
  }

  public static IReadOnlyList<SolutionView> Find(IEnumerable<(string Name, IItem? Item)> results) {
    var views = new List<SolutionView>();
    foreach (var (name, item) in results) {
      switch (item) {
        case TradingSolution trading: views.Add(Trading(name, trading)); break;
        case IClassificationSolution classification: views.Add(Classification(name, classification)); break;
        case IRegressionSolution regression: views.Add(Regression(name, regression)); break;
        case PathTSPTour tour when tour.Permutation != null && tour.Coordinates != null: views.Add(Route(name, tour)); break;
      }
    }
    return views;
  }

  /// <summary>One row per dataset row: partition, actual, predicted (and signal/profit for trading).</summary>
  public static void WritePredictionsCsv(SolutionView view, TextWriter writer) {
    if (view.Actual == null || view.Predicted == null) throw new ArgumentException($"{view.Name} has no per-row predictions.");
    bool trading = view.Kind == SolutionKind.Trading;
    writer.WriteLine(trading ? "row,partition,price_change,signal" : "row,partition,actual,predicted");
    for (int row = 0; row < view.Actual.Count; row++) {
      var partition = row >= view.Training.Start && row < view.Training.End ? "training"
                    : row >= view.Test.Start && row < view.Test.End ? "test" : "";
      writer.WriteLine(string.Join(",", row.ToString(CultureInfo.InvariantCulture), partition,
        view.Actual[row].ToString("R", CultureInfo.InvariantCulture), view.Predicted[row].ToString("R", CultureInfo.InvariantCulture)));
    }
  }

  private static SolutionView Regression(string name, IRegressionSolution s) {
    var data = s.ProblemData;
    return new SolutionView(name, s is ITimeSeriesPrognosisSolution ? SolutionKind.TimeSeries : SolutionKind.Regression,
      s.Name, ModelText(s.Model), Metrics(s), data.TargetVariable,
      data.Dataset.GetDoubleValues(data.TargetVariable).ToList(), s.EstimatedValues.ToList(),
      (data.TrainingPartition.Start, data.TrainingPartition.End), (data.TestPartition.Start, data.TestPartition.End));
  }

  private static SolutionView Classification(string name, IClassificationSolution s) {
    var data = s.ProblemData;
    return new SolutionView(name, SolutionKind.Classification, s.Name, ModelText(s.Model), Metrics(s), data.TargetVariable,
      data.Dataset.GetDoubleValues(data.TargetVariable).ToList(), s.EstimatedClassValues.ToList(),
      (data.TrainingPartition.Start, data.TrainingPartition.End), (data.TestPartition.Start, data.TestPartition.End));
  }

  private static SolutionView Trading(string name, TradingSolution s) {
    var data = s.ProblemData;
    var changes = data.Dataset.GetDoubleValues(data.PriceChangeVariable).ToList();
    // Signals are path-dependent (a position depends on the previous one). HeuristicLab evaluates
    // training and test separately, each starting fresh; doing the same keeps the equity curves
    // equal to its Profit/Sharpe results. Rows outside both partitions use the all-rows signals.
    var training = (data.TrainingPartition.Start, data.TrainingPartition.End);
    var test = (data.TestPartition.Start, data.TestPartition.End);
    var trainingSignals = s.TrainingSignals.ToList();
    var testSignals = s.TestSignals.ToList();
    var signals = s.Signals.ToList();
    for (int row = training.Start; row < training.End; row++) signals[row] = trainingSignals[row - training.Start];
    for (int row = test.Start; row < test.End; row++) signals[row] = testSignals[row - test.Start];
    IReadOnlyList<double> Equity(IEnumerable<double> returns, IEnumerable<double> partitionSignals) {
      double total = 0;
      return TradingProfit.GetProfits(returns, partitionSignals, data.TransactionCosts).Select(p => total += p).ToList();
    }
    return new SolutionView(name, SolutionKind.Trading, s.Name, ModelText(s.Model), Metrics(s), data.PriceChangeVariable,
      changes, signals, training, test,
      Equity(changes.Skip(training.Start).Take(training.End - training.Start), trainingSignals),
      Equity(changes.Skip(test.Start).Take(test.End - test.Start), testSignals));
  }

  private static SolutionView Route(string name, PathTSPTour tour) {
    var points = tour.Permutation.Select(city => (tour.Coordinates[city, 0], tour.Coordinates[city, 1])).ToList();
    if (points.Count > 0) points.Add(points[0]);
    var metrics = new Dictionary<string, double>();
    if (tour.Quality != null) metrics["Length"] = tour.Quality.Value;
    return new SolutionView(name, SolutionKind.Route, $"Tour through {tour.Permutation.Length} cities", null, metrics, Route: points);
  }

  /// <summary>Symbolic models as a formula; other models by their item name.</summary>
  public static string? ModelText(IDataAnalysisModel? model) => model switch {
    null => null,
    ISymbolicDataAnalysisModel symbolic => new InfixExpressionFormatter().Format(symbolic.SymbolicExpressionTree),
    _ => model.ToString()
  };

  /// <summary>Scalar metrics of a solution (data analysis solutions are result collections).</summary>
  private static IReadOnlyDictionary<string, double> Metrics(IDataAnalysisSolution solution) =>
    solution is IEnumerable<IResult> results
      ? results.Where(r => ItemValues.AsNumber(r.Value) != null)
               .ToDictionary(r => r.Name, r => ItemValues.AsNumber(r.Value)!.Value)
      : new Dictionary<string, double>();
}
