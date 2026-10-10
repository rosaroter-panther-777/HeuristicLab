using System.Globalization;
using HeuristicLab.Analysis;
using HeuristicLab.Next.Runtime.Visuals;

namespace HeuristicLab.Next.Runtime.Runs;

/// <summary>
/// Charts of runs, one color per group (HeuristicLab's bubble chart, box plot and chart
/// aggregation views, plus histograms and cumulative distributions): every group is a legend entry
/// that can be hidden.
/// </summary>
public static class RunCharts {
  private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
  /// <summary>X axis choice that numbers the runs (1, 2, ...) in table order.</summary>
  public const string RunIndex = "#run";

  private static string Name(RunTable table, string key) => key == RunIndex ? "Run" : table.Column(key)?.Name ?? key;

  /// <summary>One point per run: x and y are numeric columns (or the run's number for x).</summary>
  public static ChartVisual Scatter(RunTable table, IReadOnlyList<RunGroup> groups, string x, string y) {
    var index = table.Rows.Select((r, i) => (r, i)).ToDictionary(p => p.r, p => (double)(p.i + 1));
    var series = groups.Select((g, i) => new PlotSeries(g.Name, SeriesKind.Points,
      g.Rows.Select(r => (X: x == RunIndex ? index[r] : r.Number(x), Y: r.Number(y)))
        .Where(p => p.X.HasValue && p.Y.HasValue).Select(p => (p.X!.Value, p.Y!.Value)).ToList(), Rgb.Palette(i))).ToList();
    return new ChartVisual($"{Name(table, y)} over {Name(table, x)}", series, Name(table, x), Name(table, y)) {
      Notes = [$"{series.Sum(s => s.Points.Count)} runs in {groups.Count} groups"]
    };
  }

  /// <summary>Distribution per group: box from the lower to the upper quartile, median line, mean diamond, whiskers to the most extreme runs within 1.5 box lengths, outliers as points.</summary>
  public static Visual BoxPlot(RunTable table, IReadOnlyList<RunGroup> groups, string y) {
    var shapes = new List<Shape>();
    var notes = new List<string>();
    double? low = null;
    var data = groups.Select(g => (g.Name, Values: g.Rows.Select(r => r.Number(y)).OfType<double>().Where(double.IsFinite).Order().ToArray())).ToList();
    var all = data.SelectMany(d => d.Values).ToList();
    if (all.Count == 0) return new TextVisual("Box plot", $"No numeric values of {Name(table, y)}.");
    double span = Math.Max(all.Max() - all.Min(), 1e-12);
    low = all.Min() - span * 0.08;
    // room below the group names, so they do not sit on the axis
    shapes.Add(new MarkerShape(new Point2(0, low.Value - span * 0.05), new Rgb(0, 0, 0, 0), 0) { Group = "Labels" });
    for (int i = 0; i < data.Count; i++) {
      var (name, values) = data[i];
      var color = Rgb.Palette(i);
      shapes.Add(new TextShape(new Point2(i, low.Value), name, Rgb.Black) { Group = name });
      if (values.Length == 0) continue;
      var s = RunStatistics.Summary(name, values);
      double iqr = s.Q3 - s.Q1, lowFence = s.Q1 - 1.5 * iqr, highFence = s.Q3 + 1.5 * iqr;
      double whiskerLow = values.First(v => v >= lowFence), whiskerHigh = values.Last(v => v <= highFence);
      shapes.Add(new LineShape(new Point2(i, whiskerLow), new Point2(i, s.Q1), color, 1.5) { Group = name });
      shapes.Add(new LineShape(new Point2(i, s.Q3), new Point2(i, whiskerHigh), color, 1.5) { Group = name });
      shapes.Add(new LineShape(new Point2(i - 0.12, whiskerLow), new Point2(i + 0.12, whiskerLow), color, 1.5) { Group = name });
      shapes.Add(new LineShape(new Point2(i - 0.12, whiskerHigh), new Point2(i + 0.12, whiskerHigh), color, 1.5) { Group = name });
      shapes.Add(new RectShape(i - 0.3, s.Q1, 0.6, Math.Max(iqr, span * 1e-4), color.WithAlpha(90), color) { Group = name, StrokeThickness = 1.5 });
      shapes.Add(new LineShape(new Point2(i - 0.3, s.Median), new Point2(i + 0.3, s.Median), color, 2.5) { Group = name });
      shapes.Add(new MarkerShape(new Point2(i, s.Mean), color, 8, MarkerKind.Diamond) { Group = name });
      foreach (var outlier in values.Where(v => v < lowFence || v > highFence))
        shapes.Add(new MarkerShape(new Point2(i, outlier), color, 5) { Group = name });
      notes.Add($"{name}: n {s.Count}, median {s.Median.ToString("G6", Invariant)}, mean {s.Mean.ToString("G6", Invariant)}");
    }
    return new SceneVisual($"{Name(table, y)} by group", shapes, YUp: true, Uniform: false) {
      Axes = true, XAxisScale = false, YAxisTitle = Name(table, y),
      Legend = data.Select((d, i) => new LegendEntry(d.Name, Rgb.Palette(i))).ToList(),
      Notes = ["Box: quartiles · line: median · diamond: mean · whiskers: 1.5 box lengths · dots: outliers", .. notes]
    };
  }

  /// <summary>Histograms of the groups over shared bins.</summary>
  public static ChartVisual Histogram(RunTable table, IReadOnlyList<RunGroup> groups, string y, int bins = 20) {
    var values = groups.Select(g => g.Rows.Select(r => r.Number(y)).OfType<double>().Where(double.IsFinite).ToList()).ToList();
    var shared = Charts.HistogramBins(values.SelectMany(v => v), bins, exact: false);
    var series = shared is { } b
      ? groups.Select((g, i) => new PlotSeries(g.Name, SeriesKind.Histogram, Charts.Histogram(values[i], b), Rgb.Palette(i), Width: b.Width)).ToList()
      : [];
    return new ChartVisual($"Histogram of {Name(table, y)}", series, Name(table, y), "Runs");
  }

  /// <summary>Empirical cumulative distribution per group: the share of runs with at most a value.</summary>
  public static ChartVisual Cumulative(RunTable table, IReadOnlyList<RunGroup> groups, string y) {
    var series = groups.Select((g, i) => {
      var sorted = g.Rows.Select(r => r.Number(y)).OfType<double>().Where(double.IsFinite).Order().ToList();
      var points = sorted.Select((v, k) => (v, (k + 1.0) / sorted.Count)).ToList();
      if (points.Count > 0) points.Insert(0, (sorted[0], 0.0));
      return new PlotSeries(g.Name, SeriesKind.StepLine, points, Rgb.Palette(i));
    }).ToList();
    return new ChartVisual($"Cumulative distribution of {Name(table, y)}", series, Name(table, y), "Share of runs");
  }

  /// <summary>
  /// A table row (e.g. BestQuality of Qualities) of every run, one color per group (gaps between
  /// runs), optionally only the group mean (HeuristicLab's chart aggregation view).
  /// </summary>
  public static ChartVisual Curves(IReadOnlyList<RunGroup> groups, string tableName, string rowName, bool runs = true, bool mean = true) {
    var series = new List<PlotSeries>();
    string xTitle = "Index";
    for (int i = 0; i < groups.Count; i++) {
      var curves = new List<double[]>();
      foreach (var row in groups[i].Rows) {
        if (row.Run?.Results.TryGetValue(tableName, out var item) != true || item is not DataTable dataTable) continue;
        if (!string.IsNullOrEmpty(dataTable.VisualProperties.XAxisTitle)) xTitle = dataTable.VisualProperties.XAxisTitle;
        if (dataTable.Rows.TryGetValue(rowName, out var dataRow)) curves.Add(dataRow.Values.ToArray());
      }
      if (curves.Count == 0) continue;
      if (runs) {
        var points = new List<(double, double)>();
        foreach (var curve in curves) {
          if (points.Count > 0) points.Add((double.NaN, double.NaN));  // gap between runs
          points.AddRange(curve.Select((v, x) => ((double)x, v)));
        }
        series.Add(new PlotSeries(mean ? $"{groups[i].Name} (runs)" : groups[i].Name, SeriesKind.Line, points, Rgb.Palette(i).WithAlpha(mean ? (byte)110 : (byte)255)));
      }
      if (mean) {
        int length = curves.Max(c => c.Length);
        var average = Enumerable.Range(0, length).Select(x => {
          var at = curves.Where(c => x < c.Length && double.IsFinite(c[x])).Select(c => c[x]).ToList();
          return ((double)x, at.Count > 0 ? at.Average() : double.NaN);
        }).ToList();
        series.Add(new PlotSeries($"{groups[i].Name} (mean of {curves.Count})", SeriesKind.Line, average, Rgb.Palette(i)));
      }
    }
    return new ChartVisual($"{rowName} ({tableName})", series, xTitle, rowName);
  }
}
