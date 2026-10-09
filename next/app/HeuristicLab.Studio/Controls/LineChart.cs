using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HeuristicLab.Studio.Controls;

public readonly record struct ChartPoint(double X, double Y);

public sealed record ChartSeries(string Name, Color Color, IReadOnlyList<ChartPoint> Points) {
  private static readonly Color[] Palette = [
    Color.FromRgb(0x2E, 0x6F, 0xD8), Color.FromRgb(0xE0, 0x7B, 0x24), Color.FromRgb(0x3A, 0xA6, 0x5B),
    Color.FromRgb(0xC2, 0x3B, 0x4A), Color.FromRgb(0x7B, 0x52, 0xC4)
  ];
  public static Color PaletteColor(int index) => Palette[index % Palette.Length];
}

/// <summary>
/// Minimal line chart for live optimization progress: x = execution time in seconds.
/// Redraws when Series is replaced (view models publish immutable snapshots).
/// </summary>
public class LineChart : Control {
  public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesProperty =
    AvaloniaProperty.Register<LineChart, IReadOnlyList<ChartSeries>?>(nameof(Series));

  public static readonly StyledProperty<string> XAxisTitleProperty =
    AvaloniaProperty.Register<LineChart, string>(nameof(XAxisTitle), "Execution time [s]");

  public static readonly StyledProperty<IBrush?> ForegroundProperty =
    AvaloniaProperty.Register<LineChart, IBrush?>(nameof(Foreground), Brushes.Gray);

  static LineChart() {
    AffectsRender<LineChart>(SeriesProperty, XAxisTitleProperty, ForegroundProperty);
  }

  public IReadOnlyList<ChartSeries>? Series { get => GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
  public string XAxisTitle { get => GetValue(XAxisTitleProperty); set => SetValue(XAxisTitleProperty, value); }
  public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

  private const double LeftMargin = 72, RightMargin = 16, TopMargin = 28, BottomMargin = 40, FontSize = 11;
  private static readonly Typeface Font = new(FontFamily.Default);

  public override void Render(DrawingContext context) {
    var bounds = new Rect(Bounds.Size);
    var foreground = Foreground ?? Brushes.Gray;
    var plot = new Rect(LeftMargin, TopMargin,
      Math.Max(1, bounds.Width - LeftMargin - RightMargin), Math.Max(1, bounds.Height - TopMargin - BottomMargin));
    var gridPen = new Pen(new SolidColorBrush(Colors.Gray, 0.25), 1);
    var axisPen = new Pen(foreground, 1);

    var series = (Series ?? []).Where(s => s.Points.Count > 0).ToList();
    if (series.Count == 0) {
      DrawText(context, "No data yet - run the algorithm to see its progress.", foreground,
        new Point(plot.Center.X, plot.Center.Y), center: true);
      return;
    }

    var points = series.SelectMany(s => s.Points).Where(p => double.IsFinite(p.Y)).ToList();
    if (points.Count == 0) return;
    double xMin = 0, xMax = Math.Max(points.Max(p => p.X), 1e-9);
    double yMin = points.Min(p => p.Y), yMax = points.Max(p => p.Y);
    if (yMax - yMin < 1e-12) { yMin -= 1; yMax += 1; }
    var yTicks = NiceTicks(yMin, yMax, 5);
    yMin = Math.Min(yMin, yTicks[0]); yMax = Math.Max(yMax, yTicks[^1]);
    var xTicks = NiceTicks(xMin, xMax, 6);
    xMax = Math.Max(xMax, xTicks[^1]);

    Point Map(ChartPoint p) => new(
      plot.Left + (p.X - xMin) / (xMax - xMin) * plot.Width,
      plot.Bottom - (p.Y - yMin) / (yMax - yMin) * plot.Height);

    foreach (var y in yTicks) {
      var py = Map(new ChartPoint(xMin, y)).Y;
      context.DrawLine(gridPen, new Point(plot.Left, py), new Point(plot.Right, py));
      DrawText(context, Label(y), foreground, new Point(plot.Left - 6, py), alignRight: true);
    }
    foreach (var x in xTicks) {
      var px = Map(new ChartPoint(x, yMin)).X;
      context.DrawLine(gridPen, new Point(px, plot.Top), new Point(px, plot.Bottom));
      DrawText(context, Label(x), foreground, new Point(px, plot.Bottom + 10), center: true);
    }
    context.DrawLine(axisPen, plot.BottomLeft, plot.BottomRight);
    context.DrawLine(axisPen, plot.BottomLeft, plot.TopLeft);
    DrawText(context, XAxisTitle, foreground, new Point(plot.Center.X, plot.Bottom + 28), center: true);

    using (context.PushClip(plot.Inflate(1))) {
      foreach (var s in series) {
        var finite = s.Points.Where(p => double.IsFinite(p.Y)).Select(Map).ToList();
        var pen = new Pen(new SolidColorBrush(s.Color), 2);
        if (finite.Count == 1) context.DrawEllipse(pen.Brush, null, finite[0], 2.5, 2.5);
        for (int i = 1; i < finite.Count; i++) context.DrawLine(pen, finite[i - 1], finite[i]);
      }
    }

    // legend above the plot
    double lx = plot.Left;
    foreach (var s in series) {
      context.DrawRectangle(new SolidColorBrush(s.Color), null, new Rect(lx, 9, 14, 3));
      var text = MakeText(s.Name, foreground);
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

  private static string Label(double value) =>
    Math.Abs(value) >= 1e6 || (Math.Abs(value) < 1e-3 && value != 0)
      ? value.ToString("0.##E+0", CultureInfo.InvariantCulture)
      : value.ToString("0.###", CultureInfo.InvariantCulture);

  /// <summary>Round tick values (1, 2, 5 x 10^n steps) covering [min, max].</summary>
  internal static double[] NiceTicks(double min, double max, int count) {
    double range = max - min;
    double rough = range / Math.Max(1, count - 1);
    double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
    double step = new[] { 1, 2, 5, 10 }.Select(m => m * magnitude).First(s => s >= rough);
    double start = Math.Floor(min / step) * step, end = Math.Ceiling(max / step) * step;
    int n = (int)Math.Round((end - start) / step) + 1;
    return Enumerable.Range(0, n).Select(i => start + i * step).ToArray();
  }
}
