using System.Globalization;
using HeuristicLab.Core;
using HeuristicLab.Data;
using HeuristicLab.Problems.DataAnalysis;
using HeuristicLab.Problems.DataAnalysis.Symbolic;
using TradingProblemData = HeuristicLab.Problems.DataAnalysis.Trading.IProblemData;

namespace HeuristicLab.Next.Runtime.Visuals;

/// <summary>
/// Data analysis solutions as HeuristicLab's solution views show them: estimated against actual
/// values over the rows (test partition marked), a scatter plot, residuals, confusion matrices,
/// equity curves for trading, clusters; plus the model (tree and formula) for symbolic models.
/// </summary>
internal static class DataAnalysis {
  private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

  public static IReadOnlyList<Visual>? For(string name, IItem item) {
    if (item is IClusteringSolution clustering) return Clusters(clustering);
    if (item is not IDataAnalysisSolution solution) return null;
    var view = Solutions.Find([(name, item)]).FirstOrDefault();
    if (view == null) return null;
    var visuals = new List<Visual>();
    switch (view.Kind) {
      case SolutionKind.Trading:
        visuals.Add(Equity(view));
        visuals.Add(OverRows("Signals", view, view.TargetName ?? "Price change", "Signal", SeriesKind.StepLine));
        break;
      case SolutionKind.Classification:
        visuals.Add(OverRows("Estimated class values", view, view.TargetName ?? "Target", "Estimated", SeriesKind.Points));
        visuals.Add(Confusion("Confusion matrix (training)", view, view.Training));
        if (view.Test.End > view.Test.Start) visuals.Add(Confusion("Confusion matrix (test)", view, view.Test));
        break;
      default:
        visuals.Add(OverRows("Line chart", view, view.TargetName ?? "Target", "Estimated", SeriesKind.Line));
        visuals.Add(Scatter(view));
        visuals.Add(Residuals(view));
        break;
    }
    if (solution.Model is ISymbolicDataAnalysisModel symbolic) visuals.AddRange(Programs.Model(symbolic));
    return visuals;
  }

  public static IReadOnlyList<Visual>? ForProblem(object problem) {
    if (problem is not IDataAnalysisProblem { ProblemData: { } data }) return null;
    string? target = data switch {
      TradingProblemData trading => trading.PriceChangeVariable,
      IRegressionProblemData regression => regression.TargetVariable,
      IClassificationProblemData classification => classification.TargetVariable,
      _ => null
    };
    if (target == null || !data.Dataset.DoubleVariables.Contains(target)) return null;
    var values = data.Dataset.GetDoubleValues(target).ToList();
    var series = new List<PlotSeries> { new(target, SeriesKind.Line, values.Select((y, i) => ((double)i, y)).ToList(), Rgb.Palette(0)) };
    return [new ChartVisual("Target variable", series, "Row", target) {
      Markers = Partitions(data.TrainingPartition, data.TestPartition),
      Notes = [$"{data.Dataset.Rows} rows; training {data.TrainingPartition.Start}–{data.TrainingPartition.End}, test {data.TestPartition.Start}–{data.TestPartition.End}",
               $"Inputs: {string.Join(", ", data.AllowedInputVariables)}"]
    }];
  }

  private static IReadOnlyList<(double X, string Label)> Partitions(IntRange training, IntRange test) {
    var markers = new List<(double, string)>();
    if (training.End > training.Start) markers.Add((training.Start, "training"));
    if (test.End > test.Start) markers.Add((test.Start, "test"));
    return markers;
  }

  private static IReadOnlyList<(double X, string Label)> Partitions(SolutionView v) {
    var markers = new List<(double, string)>();
    if (v.Training.End > v.Training.Start) markers.Add((v.Training.Start, "training"));
    if (v.Test.End > v.Test.Start) markers.Add((v.Test.Start, "test"));
    return markers;
  }

  private static ChartVisual OverRows(string title, SolutionView v, string actualName, string predictedName, SeriesKind kind) =>
    new(title, [
      new PlotSeries(actualName, kind == SeriesKind.StepLine ? SeriesKind.Line : kind, v.Actual!.Select((y, i) => ((double)i, y)).ToList(), Rgb.Palette(0)),
      new PlotSeries(predictedName, kind, v.Predicted!.Select((y, i) => ((double)i, y)).ToList(), Rgb.Palette(1), SecondYAxis: v.Kind == SolutionKind.Trading)
    ], "Row", actualName, v.Kind == SolutionKind.Trading ? predictedName : "") { Markers = Partitions(v), Notes = MetricNotes(v) };

  private static IReadOnlyList<string> MetricNotes(SolutionView v) {
    string[] preferred = ["Pearson's R² (training)", "Pearson's R² (test)", "Mean squared error (training)", "Mean squared error (test)",
                          "Classification accuracy (training)", "Classification accuracy (test)", "Profit (training)", "Profit (test)",
                          "Sharpe ratio (training)", "Sharpe ratio (test)"];
    return preferred.Where(v.Metrics.ContainsKey).Select(m => $"{m}: {v.Metrics[m].ToString("G6", Invariant)}").ToList();
  }

  private static bool In((int Start, int End) range, int row) => row >= range.Start && row < range.End;

  /// <summary>Estimated against actual values, training and test separately, with the ideal line.</summary>
  private static ChartVisual Scatter(SolutionView v) {
    var training = new List<(double, double)>();
    var test = new List<(double, double)>();
    for (int row = 0; row < v.Actual!.Count; row++) {
      var point = (v.Actual[row], v.Predicted![row]);
      if (!double.IsFinite(point.Item1) || !double.IsFinite(point.Item2)) continue;
      if (In(v.Test, row)) test.Add(point); else if (In(v.Training, row)) training.Add(point);
    }
    var all = training.Concat(test).ToList();
    var series = new List<PlotSeries> {
      new("Training", SeriesKind.Points, training, Rgb.Palette(0)),
      new("Test", SeriesKind.Points, test, Rgb.Palette(1))
    };
    if (all.Count > 0) {
      double min = all.Min(p => Math.Min(p.Item1, p.Item2)), max = all.Max(p => Math.Max(p.Item1, p.Item2));
      series.Add(new PlotSeries("Ideal", SeriesKind.Line, [(min, min), (max, max)], Rgb.Gray));
    }
    return new ChartVisual("Scatter plot", series, v.TargetName ?? "Actual", "Estimated") { Notes = MetricNotes(v) };
  }

  private static ChartVisual Residuals(SolutionView v) {
    var training = new List<double>();
    var test = new List<double>();
    for (int row = 0; row < v.Actual!.Count; row++) {
      double residual = v.Predicted![row] - v.Actual[row];
      if (!double.IsFinite(residual)) continue;
      if (In(v.Test, row)) test.Add(residual); else if (In(v.Training, row)) training.Add(residual);
    }
    var bins = Charts.HistogramBins(training.Concat(test), 20, exact: false);
    var series = bins is { } b
      ? new List<PlotSeries> {
          new("Training", SeriesKind.Histogram, Charts.Histogram(training, b), Rgb.Palette(0), Width: b.Width),
          new("Test", SeriesKind.Histogram, Charts.Histogram(test, b), Rgb.Palette(1), Width: b.Width)
        }
      : [];
    return new ChartVisual("Residuals", series, "Residual (estimated − actual)", "Frequency");
  }

  /// <summary>Counts of (actual, estimated) class pairs in a partition, darker = more.</summary>
  private static SceneVisual Confusion(string title, SolutionView v, (int Start, int End) range) {
    var classes = v.Actual!.Concat(v.Predicted!).Where(double.IsFinite).Distinct().Order().ToList();
    var index = classes.Select((c, i) => (c, i)).ToDictionary(p => p.c, p => p.i);
    var counts = new int[classes.Count, classes.Count];
    int total = 0, correct = 0;
    for (int row = Math.Max(0, range.Start); row < Math.Min(range.End, v.Actual.Count); row++) {
      if (!index.TryGetValue(v.Actual[row], out var a) || !index.TryGetValue(v.Predicted![row], out var e)) continue;
      counts[a, e]++;
      total++;
      if (a == e) correct++;
    }
    int max = classes.Count == 0 ? 1 : Math.Max(1, counts.Cast<int>().Max());
    var shapes = new List<Shape>();
    for (int a = 0; a < classes.Count; a++) {
      shapes.Add(new TextShape(new Point2(-0.1, a + 0.5), classes[a].ToString(Invariant), Rgb.Black, TextAnchor.Right));
      shapes.Add(new TextShape(new Point2(a + 0.5, -0.15), classes[a].ToString(Invariant), Rgb.Black));
      for (int e = 0; e < classes.Count; e++) {
        double t = (double)counts[a, e] / max;
        var fill = new Rgb((byte)(255 - 200 * t), (byte)(255 - 140 * t), (byte)(255 - 40 * t));
        shapes.Add(new RectShape(e, a, 1, 1, fill, Rgb.Gray, counts[a, e].ToString(Invariant)));
      }
    }
    return new SceneVisual(title, shapes, YUp: false) {
      Notes = ["Rows: actual class; columns: estimated class",
               total > 0 ? $"{correct} of {total} correct ({(100.0 * correct / total).ToString("0.#", Invariant)} %)" : "No rows in this partition"]
    };
  }

  private static ChartVisual Equity(SolutionView v) =>
    new("Equity", [
      new PlotSeries("Training", SeriesKind.Line, v.TrainingEquity!.Select((e, i) => ((double)(v.Training.Start + i), e)).ToList(), Rgb.Palette(0)),
      new PlotSeries("Test", SeriesKind.Line, v.TestEquity!.Select((e, i) => ((double)(v.Test.Start + i), e)).ToList(), Rgb.Palette(1))
    ], "Row", "Cumulative profit") { Notes = MetricNotes(v) };

  /// <summary>Rows on the first two inputs, colored by cluster.</summary>
  private static IReadOnlyList<Visual>? Clusters(IClusteringSolution solution) {
    var data = solution.ProblemData;
    var inputs = data.AllowedInputVariables.Where(data.Dataset.DoubleVariables.Contains).Take(2).ToList();
    if (inputs.Count < 2) return null;
    var x = data.Dataset.GetDoubleValues(inputs[0]).ToList();
    var y = data.Dataset.GetDoubleValues(inputs[1]).ToList();
    var clusters = solution.ClusterValues.ToList();
    var series = clusters.Select((c, row) => (c, row)).GroupBy(p => p.c).OrderBy(g => g.Key)
      .Select(g => new PlotSeries($"Cluster {g.Key}", SeriesKind.Points, g.Select(p => (x[p.row], y[p.row])).ToList(), Rgb.Palette(g.Key - 1))).ToList();
    return [new ChartVisual("Clusters", series, inputs[0], inputs[1]) { Notes = [$"{series.Count} clusters on the first two inputs"] }];
  }
}
