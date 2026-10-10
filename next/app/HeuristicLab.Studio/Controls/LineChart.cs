using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HeuristicLab.Next.Runtime.Visuals;

namespace HeuristicLab.Studio.Controls;

public readonly record struct ChartPoint(double X, double Y);

public sealed record ChartMarker(double X, string Label);

/// <param name="PointsOnly">Draw markers without connecting lines (independent values, e.g. one per run).</param>
/// <param name="Kind">Line, step line, points, columns, bars (drawn as columns) or histogram (counts per bin).</param>
/// <param name="SecondYAxis">Scaled on its own axis on the right.</param>
/// <param name="Width">Column or histogram bin width in x units; 0 derives it from the spacing.</param>
public sealed record ChartSeries(string Name, Color Color, IReadOnlyList<ChartPoint> Points, bool PointsOnly = false,
                                 SeriesKind Kind = SeriesKind.Line, bool SecondYAxis = false, double Width = 0) {
  private static readonly Color[] Palette = [
    Color.FromRgb(0x2E, 0x6F, 0xD8), Color.FromRgb(0xE0, 0x7B, 0x24), Color.FromRgb(0x3A, 0xA6, 0x5B),
    Color.FromRgb(0xC2, 0x3B, 0x4A), Color.FromRgb(0x7B, 0x52, 0xC4)
  ];
  public static Color PaletteColor(int index) => Palette[index % Palette.Length];

  internal bool IsPoints => PointsOnly || Kind == SeriesKind.Points;
  internal bool IsColumns => Kind is SeriesKind.Columns or SeriesKind.Bars or SeriesKind.Histogram;
}

/// <summary>
/// Minimal chart for live optimization progress and HeuristicLab's tables: lines, step lines,
/// points, columns and histograms, optionally on a second y axis. Redraws when Series is replaced
/// (view models publish immutable snapshots).
/// </summary>
public class LineChart : Control {
  public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesProperty =
    AvaloniaProperty.Register<LineChart, IReadOnlyList<ChartSeries>?>(nameof(Series));

  public static readonly StyledProperty<string> XAxisTitleProperty =
    AvaloniaProperty.Register<LineChart, string>(nameof(XAxisTitle), "Execution time [s]");

  public static readonly StyledProperty<IBrush?> ForegroundProperty =
    AvaloniaProperty.Register<LineChart, IBrush?>(nameof(Foreground), Brushes.Gray);

  /// <summary>Labelled vertical lines, e.g. the start of the test partition.</summary>
  public static readonly StyledProperty<IReadOnlyList<ChartMarker>?> MarkersProperty =
    AvaloniaProperty.Register<LineChart, IReadOnlyList<ChartMarker>?>(nameof(Markers));

  public static readonly StyledProperty<string?> YAxisTitleProperty =
    AvaloniaProperty.Register<LineChart, string?>(nameof(YAxisTitle));

  public static readonly StyledProperty<string?> SecondYAxisTitleProperty =
    AvaloniaProperty.Register<LineChart, string?>(nameof(SecondYAxisTitle));

  public static readonly StyledProperty<string> EmptyTextProperty =
    AvaloniaProperty.Register<LineChart, string>(nameof(EmptyText), "No data yet - run the algorithm to see its progress.");

  static LineChart() {
    AffectsRender<LineChart>(SeriesProperty, XAxisTitleProperty, ForegroundProperty, MarkersProperty, YAxisTitleProperty,
      SecondYAxisTitleProperty, EmptyTextProperty);
  }

  public IReadOnlyList<ChartSeries>? Series { get => GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
  public string XAxisTitle { get => GetValue(XAxisTitleProperty); set => SetValue(XAxisTitleProperty, value); }
  public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
  public IReadOnlyList<ChartMarker>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
  public string? YAxisTitle { get => GetValue(YAxisTitleProperty); set => SetValue(YAxisTitleProperty, value); }
  public string? SecondYAxisTitle { get => GetValue(SecondYAxisTitleProperty); set => SetValue(SecondYAxisTitleProperty, value); }
  public string EmptyText { get => GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

  private const double LeftMargin = 72, RightMargin = 16, SecondAxisMargin = 64, TopMargin = 28, BottomMargin = 40, FontSize = 11;
  private static readonly Typeface Font = new(FontFamily.Default);

  private sealed record Axis(double Min, double Max, double[] Ticks);

  private static Axis YAxis(IEnumerable<ChartSeries> series) {
    var list = series.ToList();
    var values = list.SelectMany(s => s.Points).Select(p => p.Y).Where(double.IsFinite).ToList();
    if (values.Count == 0) return new Axis(0, 1, NiceTicks(0, 1, 5));
    double min = values.Min(), max = values.Max();
    // columns and histograms grow from zero
    if (list.Any(s => s.IsColumns)) { min = Math.Min(min, 0); max = Math.Max(max, 0); }
    if (max - min < 1e-12) { min -= 1; max += 1; }
    var ticks = NiceTicks(min, max, 5);
    return new Axis(Math.Min(min, ticks[0]), Math.Max(max, ticks[^1]), ticks);
  }

  /// <summary>Width of a series' columns in x units: given, or the smallest spacing of its points.</summary>
  private static double ColumnWidth(ChartSeries s) {
    if (s.Width > 0) return s.Width;
    var xs = s.Points.Select(p => p.X).Distinct().Order().ToList();
    double spacing = Enumerable.Range(1, Math.Max(0, xs.Count - 1)).Select(i => xs[i] - xs[i - 1]).DefaultIfEmpty(1).Min();
    return spacing * 0.8;
  }

  public override void Render(DrawingContext context) {
    var bounds = new Rect(Bounds.Size);
    var foreground = Foreground ?? Brushes.Gray;
    var series = (Series ?? []).Where(s => s.Points.Count > 0).ToList();
    var first = series.Where(s => !s.SecondYAxis).ToList();
    var second = series.Where(s => s.SecondYAxis).ToList();
    if (first.Count == 0) { first = second; second = []; }
    double right = second.Count > 0 ? SecondAxisMargin : RightMargin;
    var plot = new Rect(LeftMargin, TopMargin,
      Math.Max(1, bounds.Width - LeftMargin - right), Math.Max(1, bounds.Height - TopMargin - BottomMargin));
    var gridPen = new Pen(new SolidColorBrush(Colors.Gray, 0.25), 1);
    var axisPen = new Pen(foreground, 1);

    if (series.Count == 0 || !series.SelectMany(s => s.Points).Any(p => double.IsFinite(p.Y))) {
      DrawText(context, EmptyText, foreground, new Point(plot.Center.X, plot.Center.Y), center: true);
      return;
    }

    // x range: columns need half a column on each side
    double xMin = double.MaxValue, xMax = double.MinValue;
    foreach (var s in series) {
      double half = s.IsColumns ? ColumnWidth(s) / 2 : 0;
      foreach (var p in s.Points) { xMin = Math.Min(xMin, p.X - half); xMax = Math.Max(xMax, p.X + half); }
    }
    bool columnsOrHistogram = series.Any(s => s.IsColumns);
    if (!columnsOrHistogram) xMin = Math.Min(0, xMin);
    if (xMax - xMin < 1e-12) { xMin -= 1; xMax += 1; }
    var xTicks = NiceTicks(xMin, xMax, 6);
    if (!columnsOrHistogram) {
      // markers at the last tick would be cut in half by the plot edge
      if (series.Any(s => s.IsPoints) && xTicks.Length > 1 && xTicks[^1] <= xMax) xMax += (xTicks[1] - xTicks[0]) / 2;
      xMax = Math.Max(xMax, xTicks[^1]);
      xMin = Math.Min(xMin, xTicks[0]);
    }
    xTicks = xTicks.Where(x => x >= xMin - 1e-9 && x <= xMax + 1e-9).ToArray();

    var y1 = YAxis(first);
    var y2 = second.Count > 0 ? YAxis(second) : null;
    double MapX(double x) => plot.Left + (x - xMin) / (xMax - xMin) * plot.Width;
    double MapY(double y, Axis axis) => plot.Bottom - (y - axis.Min) / (axis.Max - axis.Min) * plot.Height;

    foreach (var y in y1.Ticks) {
      var py = MapY(y, y1);
      context.DrawLine(gridPen, new Point(plot.Left, py), new Point(plot.Right, py));
      DrawText(context, Label(y), foreground, new Point(plot.Left - 6, py), alignRight: true);
    }
    if (y2 != null)
      foreach (var y in y2.Ticks)
        DrawText(context, Label(y), foreground, new Point(plot.Right + 6, MapY(y, y2)));
    foreach (var x in xTicks) {
      var px = MapX(x);
      context.DrawLine(gridPen, new Point(px, plot.Top), new Point(px, plot.Bottom));
      DrawText(context, Label(x), foreground, new Point(px, plot.Bottom + 10), center: true);
    }
    context.DrawLine(axisPen, plot.BottomLeft, plot.BottomRight);
    context.DrawLine(axisPen, plot.BottomLeft, plot.TopLeft);
    if (y2 != null) context.DrawLine(axisPen, plot.BottomRight, plot.TopRight);
    DrawText(context, XAxisTitle, foreground, new Point(plot.Center.X, plot.Bottom + 28), center: true);
    var yTitles = string.Join("   |   ", new[] { YAxisTitle, y2 != null ? SecondYAxisTitle + " (right)" : null }.Where(t => !string.IsNullOrEmpty(t)));
    if (yTitles.Length > 0) DrawText(context, yTitles, foreground, new Point(bounds.Width - 4, 10), alignRight: true);

    var markerPen = new Pen(foreground, 1, new DashStyle([4, 3], 0));
    foreach (var marker in Markers ?? []) {
      if (marker.X < xMin || marker.X > xMax) continue;
      var px = MapX(marker.X);
      context.DrawLine(markerPen, new Point(px, plot.Top), new Point(px, plot.Bottom));
      DrawText(context, marker.Label, foreground, new Point(px + 4, plot.Top + 8));
    }

    using (context.PushClip(plot.Inflate(1))) {
      // columns first, so lines and points stay visible on top
      foreach (var s in series.OrderBy(s => s.IsColumns ? 0 : 1)) {
        var axis = s.SecondYAxis && y2 != null ? y2 : y1;
        var finite = s.Points.Where(p => double.IsFinite(p.Y)).ToList();
        var brush = new SolidColorBrush(s.Color);
        var pen = new Pen(brush, 2);
        if (s.IsColumns) {
          double width = ColumnWidth(s), baseline = MapY(Math.Clamp(0, axis.Min, axis.Max), axis);
          // overlapping histograms stay readable when translucent
          var fill = new SolidColorBrush(s.Color, s.Kind == SeriesKind.Histogram && series.Count(o => o.IsColumns) > 1 ? 0.55 : 0.85);
          var outline = new Pen(new SolidColorBrush(Colors.White, 0.7), 0.5);
          foreach (var p in finite) {
            double left = MapX(p.X - width / 2), rightEdge = MapX(p.X + width / 2), top = MapY(p.Y, axis);
            context.DrawRectangle(fill, outline, new Rect(new Point(left, Math.Min(top, baseline)), new Point(rightEdge, Math.Max(top, baseline))));
          }
          continue;
        }
        var mapped = finite.Select(p => new Point(MapX(p.X), MapY(p.Y, axis))).ToList();
        if (s.IsPoints || mapped.Count == 1) {
          foreach (var p in mapped) context.DrawEllipse(brush, null, p, 3.5, 3.5);
          continue;
        }
        for (int i = 1; i < mapped.Count; i++) {
          if (s.Kind == SeriesKind.StepLine) {
            var corner = new Point(mapped[i].X, mapped[i - 1].Y);
            context.DrawLine(pen, mapped[i - 1], corner);
            context.DrawLine(pen, corner, mapped[i]);
          } else context.DrawLine(pen, mapped[i - 1], mapped[i]);
        }
      }
    }

    // legend above the plot
    double lx = plot.Left;
    foreach (var s in series) {
      context.DrawRectangle(new SolidColorBrush(s.Color), null, s.IsColumns ? new Rect(lx, 6, 12, 9) : new Rect(lx, 9, 14, 3));
      var text = MakeText(s.Name + (s.SecondYAxis && y2 != null ? " (right)" : ""), foreground);
      context.DrawText(text, new Point(lx + 18, 10 - text.Height / 2 + 1));
      lx += 18 + text.Width + 18;
    }
  }

  private static FormattedText MakeText(string text, IBrush brush) =>
    new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, FontSize, brush);

  private static void DrawText(DrawingContext context, string text, IBrush brush, Point anchor,
                               bool alignRight = false, bool center = false) {
    var formatted = MakeText(text, brush);
    double x = alignRight ? anchor.X - formatted.Width : center ? anchor.X - formatted.Width / 2 : anchor.X;
    context.DrawText(formatted, new Point(x, anchor.Y - formatted.Height / 2));
  }

  internal static string Label(double value) =>
    Math.Abs(value) >= 1e6 || (Math.Abs(value) < 1e-3 && value != 0)
      ? value.ToString("0.##E+0", CultureInfo.InvariantCulture)
      : value.ToString("0.###", CultureInfo.InvariantCulture);

  /// <summary>Round tick values (1, 2, 5 x 10^n steps) covering [min, max].</summary>
  internal static double[] NiceTicks(double min, double max, int count) {
    double range = max - min;
    if (!(range > 0) || !double.IsFinite(range)) return [min];
    double rough = range / Math.Max(1, count - 1);
    double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
    double step = new[] { 1, 2, 5, 10 }.Select(m => m * magnitude).First(s => s >= rough);
    double start = Math.Floor(min / step) * step, end = Math.Ceiling(max / step) * step;
    int n = (int)Math.Round((end - start) / step) + 1;
    return Enumerable.Range(0, n).Select(i => start + i * step).ToArray();
  }
}
