using System.Collections;
using System.Runtime.CompilerServices;
using HeuristicLab.Analysis;
using HeuristicLab.Core;
using DrawingColor = System.Drawing.Color;

namespace HeuristicLab.Next.Runtime.Visuals;

/// <summary>HeuristicLab's analysis tables and plots as charts, honoring their visual properties.</summary>
internal static class Charts {
  public static IReadOnlyList<Visual>? For(IItem item) => item switch {
    DataTable table => [Table(table)],
    ScatterPlot plot => [Scatter(plot)],
    HeatMap map => [HeatMapScene(map)],
    DataTableHistory history => Last(history, "tables", t => Table(t)),
    ScatterPlotHistory history => Last(history, "plots", p => Scatter(p)),
    HeatMapHistory history => Last(history, "heat maps", m => HeatMapScene(m)),
    _ when IsIndexedTable(item) => [IndexedTable(item)],
    _ => null
  };

  private static Visual[] Last<T>(IEnumerable<T> history, string what, Func<T, Visual> build) {
    var all = history.ToArray();
    if (all.Length == 0) return [];
    var last = build(all[^1]);
    return [last with { Notes = [$"Latest of {all.Length} {what}", .. last.Notes] }];
  }

  public static ChartVisual Table(DataTable table) {
    var properties = table.VisualProperties;
    var rows = table.Rows.ToArray();
    var histogramRows = rows.Where(r => r.VisualProperties.ChartType == DataRowVisualProperties.DataRowChartType.Histogram).ToList();
    var bins = histogramRows.Count > 0 ? HistogramBins(histogramRows.SelectMany(r => r.Values.ToArray()), properties) : null;
    var series = new List<PlotSeries>();
    foreach (var (row, index) in rows.Select((r, i) => (r, i))) {
      var visual = row.VisualProperties;
      var values = row.Values.ToArray();
      var color = ColorOf(visual.Color, index);
      var name = string.IsNullOrEmpty(visual.DisplayName) ? row.Name : visual.DisplayName;
      if (visual.ChartType == DataRowVisualProperties.DataRowChartType.Histogram) {
        if (bins == null) continue;
        series.Add(new PlotSeries(name, SeriesKind.Histogram, Histogram(values, bins.Value), color, visual.SecondYAxis, bins.Value.Width));
        continue;
      }
      // HeuristicLab counts from 1 unless the row says otherwise
      int offset = visual.StartIndexZero ? 0 : 1;
      var points = values.Select((y, i) => (X: (double)(i + offset), Y: y)).Where(p => double.IsFinite(p.Y)).ToList();
      series.Add(new PlotSeries(name, Kind(visual.ChartType), points, color, visual.SecondYAxis));
    }
    bool onlyHistograms = histogramRows.Count == rows.Length && rows.Length > 0;
    string xTitle = !string.IsNullOrEmpty(properties.XAxisTitle) ? properties.XAxisTitle : onlyHistograms ? "Value" : "Index";
    string yTitle = !string.IsNullOrEmpty(properties.YAxisTitle) ? properties.YAxisTitle : onlyHistograms ? "Frequency" : "";
    return new ChartVisual(string.IsNullOrEmpty(properties.Title) ? table.Name : properties.Title, series, xTitle, yTitle,
      properties.SecondYAxisTitle ?? "") { LogX = properties.XAxisLogScale, LogY = properties.YAxisLogScale };
  }

  private static SeriesKind Kind(DataRowVisualProperties.DataRowChartType type) => type switch {
    DataRowVisualProperties.DataRowChartType.Columns => SeriesKind.Columns,
    DataRowVisualProperties.DataRowChartType.Points => SeriesKind.Points,
    DataRowVisualProperties.DataRowChartType.Bars => SeriesKind.Bars,
    DataRowVisualProperties.DataRowChartType.StepLine => SeriesKind.StepLine,
    DataRowVisualProperties.DataRowChartType.Histogram => SeriesKind.Histogram,
    _ => SeriesKind.Line
  };

  internal readonly record struct Bins(double Start, double Width, int Count);

  /// <summary>
  /// Bins shared by all histogram rows of a table, as HeuristicLab's table view computes them:
  /// HistogramBins intervals over the value range, widened to a "human" width unless exact bins
  /// are requested.
  /// </summary>
  internal static Bins? HistogramBins(IEnumerable<double> values, DataTableVisualProperties properties) =>
    HistogramBins(values, properties.HistogramBins, properties.HistogramExactBins);

  internal static Bins? HistogramBins(IEnumerable<double> values, int binCount, bool exact) {
    var valid = values.Where(double.IsFinite).ToList();
    if (valid.Count == 0) return null;
    double min = valid.Min(), max = valid.Max();
    double width = (max - min) / Math.Max(1, binCount);
    if (width <= 0) return new Bins(min - 0.5, 1, 1);
    if (!exact) {
      width = HumanRound(width);
      min = Math.Floor(min / width) * width;
      max = Math.Ceiling(max / width) * width;
    }
    // the last interval includes the maximum
    int count = Math.Max(1, (int)Math.Floor((max - min) / width + 1e-9) + 1);
    return new Bins(min, width, count);
  }

  /// <summary>Points at bin centers with the number of values in [start, start + width).</summary>
  internal static List<(double X, double Y)> Histogram(IEnumerable<double> values, Bins bins) {
    var counts = new double[bins.Count];
    foreach (var v in values) {
      if (!double.IsFinite(v)) continue;
      int bin = (int)Math.Floor((v - bins.Start) / bins.Width + 1e-9);
      counts[Math.Clamp(bin, 0, bins.Count - 1)]++;
    }
    return counts.Select((c, i) => (bins.Start + (i + 0.5) * bins.Width, c)).ToList();
  }

  private static double HumanRound(double range) {
    double base10 = Math.Pow(10.0, Math.Floor(Math.Log10(range)));
    double rounding = range / base10;
    rounding = rounding <= 1.5 ? 1 : rounding <= 2.25 ? 2 : rounding <= 3.75 ? 2.5 : rounding <= 7.5 ? 5 : 10;
    return rounding * base10;
  }

  private static bool IsIndexedTable(IItem item) {
    for (var type = item.GetType(); type != null; type = type.BaseType)
      if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IndexedDataTable<>)) return true;
    return false;
  }

  /// <summary>IndexedDataTable&lt;T&gt;: rows of (index, value) pairs, e.g. quality over evaluations.</summary>
  private static ChartVisual IndexedTable(IItem item) {
    dynamic table = item;
    DataTableVisualProperties properties = table.VisualProperties;
    var series = new List<PlotSeries>();
    int index = 0;
    foreach (var row in ((IEnumerable)table.Rows).Cast<object>().ToArray()) {
      dynamic r = row;
      DataRowVisualProperties visual = r.VisualProperties;
      var points = new List<(double, double)>();
      foreach (var pair in ((IEnumerable)r.Values).Cast<ITuple>().ToArray()) {
        double y = Convert.ToDouble(pair[1]);
        if (double.IsFinite(y)) points.Add((Convert.ToDouble(pair[0]), y));
      }
      string name = string.IsNullOrEmpty(visual.DisplayName) ? (string)r.Name : visual.DisplayName;
      series.Add(new PlotSeries(name, Kind(visual.ChartType), points, ColorOf(visual.Color, index++), visual.SecondYAxis));
    }
    string title = string.IsNullOrEmpty(properties.Title) ? ((INamedItem)item).Name : properties.Title;
    return new ChartVisual(title, series, string.IsNullOrEmpty(properties.XAxisTitle) ? "Index" : properties.XAxisTitle,
      properties.YAxisTitle ?? "", properties.SecondYAxisTitle ?? "") { LogX = properties.XAxisLogScale, LogY = properties.YAxisLogScale };
  }

  public static ChartVisual Scatter(ScatterPlot plot) {
    var properties = plot.VisualProperties;
    var series = plot.Rows.ToArray().Select((row, i) => new PlotSeries(
      string.IsNullOrEmpty(row.VisualProperties.DisplayName) ? row.Name : row.VisualProperties.DisplayName,
      SeriesKind.Points, row.Points.ToArray().Select(p => (p.X, p.Y)).ToList(), ColorOf(row.VisualProperties.Color, i))).ToList();
    return new ChartVisual(string.IsNullOrEmpty(properties.Title) ? plot.Name : properties.Title, series,
      properties.XAxisTitle ?? "", properties.YAxisTitle ?? "") { LogX = properties.XAxisLogScale, LogY = properties.YAxisLogScale };
  }

  /// <summary>A heat map as colored cells (row 0 at the top), scaled between its minimum and maximum.</summary>
  public static SceneVisual HeatMapScene(HeatMap map) {
    int rows = map.Rows, columns = map.Columns;
    double min = map.Minimum, max = map.Maximum;
    if (!(max > min)) {
      var values = Enumerable.Range(0, rows * columns).Select(i => map[i / columns, i % columns]).Where(double.IsFinite).ToList();
      min = values.Count > 0 ? values.Min() : 0;
      max = values.Count > 0 ? values.Max() : 1;
    }
    var pixels = new Rgb[rows * columns];
    for (int r = 0; r < rows; r++)
      for (int c = 0; c < columns; c++)
        pixels[r * columns + c] = Rgb.Ramp(max > min ? (map[r, c] - min) / (max - min) : 0.5);
    return new SceneVisual(string.IsNullOrEmpty(map.Title) ? "Heat map" : map.Title,
      [new RasterShape(0, 0, columns, rows, rows, columns, pixels)], YUp: false, Uniform: false) {
      Notes = [$"{rows} × {columns} cells, from {min:G4} (dark blue) to {max:G4} (dark red)"]
    };
  }

  internal static Rgb ColorOf(DrawingColor color, int index) =>
    color.IsEmpty || color.A == 0 ? Rgb.Palette(index) : new Rgb(color.R, color.G, color.B);
}
