using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using HeuristicLab.Next.Runtime.Visuals;

namespace HeuristicLab.Studio.Controls;

/// <summary>
/// Draws a <see cref="SceneVisual"/> (tours, routes, packings, trees, grids, Gantt charts,
/// landscapes): fitted to the control, uniformly or stretched, y up or down. Wheel zooms around
/// the pointer, dragging moves, double-click resets; the view is kept while the scene is updated
/// live and reset when another picture is shown.
/// </summary>
public class SceneView : Control {
  public static readonly StyledProperty<SceneVisual?> SceneProperty =
    AvaloniaProperty.Register<SceneView, SceneVisual?>(nameof(Scene));

  public static readonly StyledProperty<IBrush?> ForegroundProperty =
    AvaloniaProperty.Register<SceneView, IBrush?>(nameof(Foreground), Brushes.Gray);

  static SceneView() {
    AffectsRender<SceneView>(SceneProperty, ForegroundProperty);
    SceneProperty.Changed.AddClassHandler<SceneView>((view, e) => {
      if ((e.OldValue as SceneVisual)?.Title != (e.NewValue as SceneVisual)?.Title) view.ResetView();
    });
  }

  public SceneVisual? Scene { get => GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
  public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

  private double zoom = 1;
  private Vector pan;
  private Point? dragStart;
  private Vector panAtDragStart;

  public SceneView() {
    ClipToBounds = true;
    Background = Brushes.Transparent;
  }

  /// <summary>Hit testing needs a background.</summary>
  public IBrush? Background { get; set; }

  private void ResetView() {
    zoom = 1;
    pan = default;
    InvalidateVisual();
  }

  protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
    base.OnPointerWheelChanged(e);
    var at = e.GetPosition(this);
    double factor = e.Delta.Y > 0 ? 1.2 : 1 / 1.2;
    double newZoom = Math.Clamp(zoom * factor, 0.2, 200);
    factor = newZoom / zoom;
    // keep the point under the pointer in place
    var center = new Point(Bounds.Width / 2, Bounds.Height / 2) + pan;
    pan += (at - center) * (1 - factor);
    zoom = newZoom;
    InvalidateVisual();
    e.Handled = true;
  }

  protected override void OnPointerPressed(PointerPressedEventArgs e) {
    base.OnPointerPressed(e);
    if (e.ClickCount == 2) { ResetView(); return; }
    dragStart = e.GetPosition(this);
    panAtDragStart = pan;
    e.Pointer.Capture(this);
  }

  protected override void OnPointerMoved(PointerEventArgs e) {
    base.OnPointerMoved(e);
    if (dragStart is not { } start) return;
    pan = panAtDragStart + (e.GetPosition(this) - start);
    InvalidateVisual();
  }

  protected override void OnPointerReleased(PointerReleasedEventArgs e) {
    base.OnPointerReleased(e);
    dragStart = null;
    e.Pointer.Capture(null);
  }

  private static readonly Typeface Font = new(FontFamily.Default);

  private static FormattedText Text(string text, IBrush brush, double size = 11) =>
    new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, brush);

  private static Color ColorOf(Rgb c) => Color.FromArgb(c.A, c.R, c.G, c.B);

  /// <summary>Black text on light fills, white text on dark ones.</summary>
  private static IBrush LabelBrush(Rgb? fill) =>
    fill is { } f && f.A > 100 && 0.299 * f.R + 0.587 * f.G + 0.114 * f.B < 140 ? Brushes.White : Brushes.Black;

  private readonly ConditionalWeakTable<Rgb[], WriteableBitmap> bitmaps = new();

  private WriteableBitmap Bitmap(RasterShape raster) => bitmaps.GetValue(raster.Pixels, pixels => {
    var bitmap = new WriteableBitmap(new PixelSize(raster.Columns, raster.Rows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
    using var buffer = bitmap.Lock();
    var row = new byte[raster.Columns * 4];
    for (int r = 0; r < raster.Rows; r++) {
      for (int c = 0; c < raster.Columns; c++) {
        var p = pixels[r * raster.Columns + c];
        row[c * 4] = p.B; row[c * 4 + 1] = p.G; row[c * 4 + 2] = p.R; row[c * 4 + 3] = p.A;
      }
      System.Runtime.InteropServices.Marshal.Copy(row, 0, buffer.Address + r * buffer.RowBytes, row.Length);
    }
    return bitmap;
  });

  private static (double MinX, double MinY, double MaxX, double MaxY)? WorldBounds(IEnumerable<Shape> shapes) {
    double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
    void Add(double x, double y) {
      if (!double.IsFinite(x) || !double.IsFinite(y)) return;
      minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
    }
    foreach (var shape in shapes) {
      switch (shape) {
        case LineShape l: Add(l.From.X, l.From.Y); Add(l.To.X, l.To.Y); break;
        case PathShape p: foreach (var q in p.Points) Add(q.X, q.Y); break;
        case MarkerShape m: Add(m.At.X, m.At.Y); break;
        case RectShape r: Add(r.X, r.Y); Add(r.X + r.Width, r.Y + r.Height); break;
        case EllipseShape e: Add(e.X, e.Y); Add(e.X + e.Width, e.Y + e.Height); break;
        case RasterShape r: Add(r.X, r.Y); Add(r.X + r.Width, r.Y + r.Height); break;
        // labels placed in scene coordinates (row and column names) must stay inside the view
        case TextShape t: Add(t.At.X, t.At.Y); break;
      }
    }
    return minX <= maxX ? (minX, minY, maxX, maxY) : null;
  }

  public override void Render(DrawingContext context) {
    var foreground = Foreground ?? Brushes.Gray;
    if (Background != null) context.FillRectangle(Background, new Rect(Bounds.Size));
    var scene = Scene;
    if (scene == null || WorldBounds(scene.Shapes) is not { } world) {
      var empty = Text("Nothing to draw yet.", foreground);
      context.DrawText(empty, new Point((Bounds.Width - empty.Width) / 2, (Bounds.Height - empty.Height) / 2));
      return;
    }

    // room for labels outside the drawing (row names, axis ticks) and for the legend
    double rightAnchored = scene.Shapes.OfType<TextShape>().Where(t => t.Anchor == TextAnchor.Right)
      .Select(t => Text(t.Text, foreground, t.Size).Width).DefaultIfEmpty(0).Max();
    double left = 16 + Math.Max(rightAnchored, scene.Axes ? 56 : 0);
    double top = 16 + (scene.Legend.Count > 0 ? 20 : 0) + (scene.Shapes.OfType<TextShape>().Any() ? 12 : 0);
    double bottom = scene.Axes ? 44 : 16, right = 24;
    var plot = new Rect(left, top, Math.Max(1, Bounds.Width - left - right), Math.Max(1, Bounds.Height - top - bottom));

    double width = Math.Max(world.MaxX - world.MinX, 1e-9), height = Math.Max(world.MaxY - world.MinY, 1e-9);
    if (world.MaxX - world.MinX < 1e-9) width = Math.Max(1, height);
    if (world.MaxY - world.MinY < 1e-9) height = Math.Max(1, width);
    double sx = plot.Width / width, sy = plot.Height / height;
    if (scene.Uniform) sx = sy = Math.Min(sx, sy);
    sx *= zoom; sy *= zoom;
    double cx = (world.MinX + world.MaxX) / 2, cy = (world.MinY + world.MaxY) / 2;
    var screenCenter = plot.Center + pan;
    Point Map(double x, double y) => new(screenCenter.X + (x - cx) * sx, scene.YUp ? screenCenter.Y - (y - cy) * sy : screenCenter.Y + (y - cy) * sy);
    Point MapP(Point2 p) => Map(p.X, p.Y);
    Rect MapRect(double x, double y, double w, double h) => new Rect(Map(x, y), Map(x + w, y + h)).Normalize();

    using (context.PushClip(new Rect(Bounds.Size))) {
      foreach (var shape in scene.Shapes) {
        switch (shape) {
          case RasterShape raster:
            // cells stay crisp: a lawn tile or cost cell is one color, not a blur
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
              context.DrawImage(Bitmap(raster), new Rect(0, 0, raster.Columns, raster.Rows), MapRect(raster.X, raster.Y, raster.Width, raster.Height));
            break;
          case RectShape r: {
              var rect = MapRect(r.X, r.Y, r.Width, r.Height);
              context.DrawRectangle(r.Fill is { } fill ? new SolidColorBrush(ColorOf(fill)) : null,
                r.Stroke is { } stroke ? new Pen(new SolidColorBrush(ColorOf(stroke)), r.StrokeThickness) : null, rect);
              if (!string.IsNullOrEmpty(r.Label)) {
                var label = Text(r.Label, LabelBrush(r.Fill));
                if (label.Width <= rect.Width - 2 && label.Height <= rect.Height + 2)
                  context.DrawText(label, new Point(rect.Center.X - label.Width / 2, rect.Center.Y - label.Height / 2));
              }
              break;
            }
          case EllipseShape e: {
              var rect = MapRect(e.X, e.Y, e.Width, e.Height);
              context.DrawEllipse(e.Fill is { } fill ? new SolidColorBrush(ColorOf(fill)) : null,
                e.Stroke is { } stroke ? new Pen(new SolidColorBrush(ColorOf(stroke)), 1) : null, rect.Center, rect.Width / 2, rect.Height / 2);
              break;
            }
          case LineShape l:
            context.DrawLine(new Pen(new SolidColorBrush(ColorOf(l.Color)), l.Thickness), MapP(l.From), MapP(l.To));
            break;
          case PathShape p when p.Points.Count > 1: {
              var geometry = new StreamGeometry();
              using (var g = geometry.Open()) {
                g.BeginFigure(MapP(p.Points[0]), p.Fill != null);
                foreach (var q in p.Points.Skip(1)) g.LineTo(MapP(q));
                g.EndFigure(p.Closed);
              }
              context.DrawGeometry(p.Fill is { } fill ? new SolidColorBrush(ColorOf(fill)) : null,
                new Pen(new SolidColorBrush(ColorOf(p.Color)), p.Thickness, lineJoin: PenLineJoin.Round), geometry);
              break;
            }
          case MarkerShape m: {
              var at = MapP(m.At);
              var brush = new SolidColorBrush(ColorOf(m.Color));
              double h = m.Size / 2;
              switch (m.Kind) {
                case MarkerKind.Square: context.FillRectangle(brush, new Rect(at.X - h, at.Y - h, m.Size, m.Size)); break;
                case MarkerKind.Diamond: {
                    var diamond = new StreamGeometry();
                    using (var g = diamond.Open()) {
                      g.BeginFigure(new Point(at.X, at.Y - h), true);
                      g.LineTo(new Point(at.X + h, at.Y)); g.LineTo(new Point(at.X, at.Y + h)); g.LineTo(new Point(at.X - h, at.Y));
                      g.EndFigure(true);
                    }
                    context.DrawGeometry(brush, new Pen(Brushes.White, 1), diamond);
                    break;
                  }
                default: context.DrawEllipse(brush, new Pen(Brushes.White, 1), at, h, h); break;
              }
              if (!string.IsNullOrEmpty(m.Label)) {
                var label = Text(m.Label, foreground);
                context.DrawText(label, new Point(at.X + h + 3, at.Y - label.Height / 2));
              }
              break;
            }
          case TextShape t: {
              var at = MapP(t.At);
              var label = Text(t.Text, t.Color == Rgb.Black ? foreground : new SolidColorBrush(ColorOf(t.Color)), t.Size);
              double x = t.Anchor switch { TextAnchor.Right => at.X - label.Width, TextAnchor.Left => at.X, _ => at.X - label.Width / 2 };
              context.DrawText(label, new Point(x, at.Y - label.Height / 2));
              break;
            }
        }
      }
    }

    if (scene.Axes) DrawAxes(context, scene, foreground, plot, Map, sx, sy, screenCenter, cx, cy);

    double lx = left;
    foreach (var entry in scene.Legend) {
      context.FillRectangle(new SolidColorBrush(ColorOf(entry.Color)), new Rect(lx, 10, 12, 10));
      var text = Text(entry.Label, foreground);
      context.DrawText(text, new Point(lx + 16, 15 - text.Height / 2));
      lx += 16 + text.Width + 16;
    }
  }

  /// <summary>Scales along the bottom and left edges for the part of the scene that is visible.</summary>
  private void DrawAxes(DrawingContext context, SceneVisual scene, IBrush foreground, Rect plot, Func<double, double, Point> map,
                        double sx, double sy, Point screenCenter, double cx, double cy) {
    double WorldX(double px) => cx + (px - screenCenter.X) / sx;
    double WorldY(double py) => scene.YUp ? cy - (py - screenCenter.Y) / sy : cy + (py - screenCenter.Y) / sy;
    var pen = new Pen(foreground, 1);
    var grid = new Pen(new SolidColorBrush(Colors.Gray, 0.25), 1);
    context.DrawLine(pen, plot.BottomLeft, plot.BottomRight);
    context.DrawLine(pen, plot.BottomLeft, plot.TopLeft);
    double x0 = WorldX(plot.Left), x1 = WorldX(plot.Right);
    if (scene.XAxisScale) foreach (var x in LineChart.NiceTicks(Math.Min(x0, x1), Math.Max(x0, x1), 6)) {
      double px = map(x, 0).X;
      if (px < plot.Left - 0.5 || px > plot.Right + 0.5) continue;
      context.DrawLine(grid, new Point(px, plot.Top), new Point(px, plot.Bottom));
      var label = Text(LineChart.Label(x), foreground);
      context.DrawText(label, new Point(px - label.Width / 2, plot.Bottom + 4));
    }
    if (!scene.Shapes.OfType<TextShape>().Any(t => t.Anchor == TextAnchor.Right)) {
      double y0 = WorldY(plot.Bottom), y1 = WorldY(plot.Top);
      foreach (var y in LineChart.NiceTicks(Math.Min(y0, y1), Math.Max(y0, y1), 5)) {
        double py = map(0, y).Y;
        if (py < plot.Top - 0.5 || py > plot.Bottom + 0.5) continue;
        context.DrawLine(grid, new Point(plot.Left, py), new Point(plot.Right, py));
        var label = Text(LineChart.Label(y), foreground);
        context.DrawText(label, new Point(plot.Left - label.Width - 6, py - label.Height / 2));
      }
    }
    if (!string.IsNullOrEmpty(scene.XAxisTitle)) {
      var title = Text(scene.XAxisTitle, foreground);
      context.DrawText(title, new Point(plot.Center.X - title.Width / 2, plot.Bottom + 22));
    }
    if (!string.IsNullOrEmpty(scene.YAxisTitle)) {
      var title = Text(scene.YAxisTitle, foreground);
      context.DrawText(title, new Point(Math.Max(2, plot.Left - title.Width - 6), Math.Max(2, plot.Top - 18)));
    }
  }
}
