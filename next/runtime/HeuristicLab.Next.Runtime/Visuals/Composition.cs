namespace HeuristicLab.Next.Runtime.Visuals;

public enum LayerKind { Earlier, Later, RangeStart, RangeEnd }

/// <param name="Age">0: next to the current solution, 1: farthest away (lightest shade).</param>
public sealed record SolutionLayer(Visual Visual, LayerKind Kind, double Age = 0);

/// <summary>
/// Several solutions of one problem in one picture (the detailed analysis): the current solution in
/// red with what it has that its baselines (the solutions it was made from) do not, in yellow on top;
/// earlier iterations in shades of blue and later ones in shades of green, darker the closer they
/// are; the start and end of a selected range as wide dark blue and medium dark green strokes under
/// the current one, so overlapping parts stay distinguishable. Only shapes marked IsSolution are
/// recolored; the problem's data (locations, grids, landscapes) is drawn once, from the current picture.
/// </summary>
public static class Composition {
  public static readonly Rgb Current = new(0xD6, 0x27, 0x28);
  public static readonly Rgb Changed = new(0xF5, 0xB8, 0x00);
  private static readonly Rgb DarkBlue = new(0x1F, 0x4E, 0x9C), LightBlue = new(0xC2, 0xD6, 0xF2);
  private static readonly Rgb DarkGreen = new(0x1E, 0x7B, 0x34), LightGreen = new(0xC4, 0xE8, 0xCA);

  public static Rgb Earlier(double age) => Mix(DarkBlue, LightBlue, age);
  public static Rgb Later(double age) => Mix(DarkGreen, LightGreen, age);
  public static readonly Rgb RangeStart = new(0x14, 0x34, 0x6E), RangeEnd = new(0x14, 0x5C, 0x26);

  private static Rgb Mix(Rgb a, Rgb b, double t) {
    t = Math.Clamp(t, 0, 1);
    byte M(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
    return new Rgb(M(a.R, b.R), M(a.G, b.G), M(a.B, b.B));
  }

  public static Visual Compose(Visual current, IEnumerable<Visual> baselines, IEnumerable<SolutionLayer> layers) {
    if (current is not SceneVisual scene || !scene.Layered) return current;
    var layerList = layers.Where(l => l.Visual is SceneVisual { Layered: true }).ToList();
    var solution = scene.Shapes.Where(s => s.IsSolution && s is not RasterShape).ToList();
    var background = scene.Shapes.Where(s => !s.IsSolution && s is not MarkerShape and not TextShape || s is RasterShape).ToList();
    var foreground = scene.Shapes.Where(s => !s.IsSolution && s is MarkerShape or TextShape).ToList();

    var shapes = new List<Shape>(background);
    // lightest first: the far iterations at the bottom, then the range ends
    foreach (var layer in layerList.OrderBy(l => l.Kind is LayerKind.RangeStart or LayerKind.RangeEnd ? 1 : 0)
                                   .ThenByDescending(l => l.Age).ThenBy(l => l.Kind == LayerKind.RangeEnd ? 1 : 0)) {
      var (color, width) = layer.Kind switch {
        LayerKind.Earlier => (Earlier(layer.Age), 1.5),
        LayerKind.Later => (Later(layer.Age), 1.5),
        LayerKind.RangeStart => (RangeStart, 8.0),
        _ => (RangeEnd, 4.5)
      };
      string group = layer.Kind switch {
        LayerKind.Earlier => "Earlier iterations", LayerKind.Later => "Later iterations", LayerKind.RangeStart => "Range start", _ => "Range end"
      };
      shapes.AddRange(((SceneVisual)layer.Visual).Shapes.Where(s => s.IsSolution).Select(s => Restyle(s, color, width)).OfType<Shape>()
        .Select(s => s with { Group = group }));
    }

    var bases = baselines.OfType<SceneVisual>().Where(b => b.Layered).ToList();
    var changed = bases.Count > 0 ? Changes(solution, bases.SelectMany(b => b.Shapes.Where(s => s.IsSolution)).ToList()) : [];
    const string changedGroup = "Changed in this iteration";
    shapes.AddRange(changed.OfType<MarkerShape>().Select(s => s with { Group = changedGroup }));  // halos under the current markers
    shapes.AddRange(solution.Select(Red).Select(s => s with { Group = "Current" }));
    shapes.AddRange(changed.Where(s => s is not MarkerShape).Select(s => s with { Group = changedGroup }));
    shapes.AddRange(foreground);

    var legend = new List<LegendEntry> { new("Current", Current) };
    if (changed.Count > 0) legend.Add(new(changedGroup, Changed));
    if (layerList.Any(l => l.Kind == LayerKind.Earlier)) legend.Add(new("Earlier iterations", Earlier(0.3)));
    if (layerList.Any(l => l.Kind == LayerKind.Later)) legend.Add(new("Later iterations", Later(0.3)));
    if (layerList.Any(l => l.Kind == LayerKind.RangeStart)) legend.Add(new("Range start", RangeStart));
    if (layerList.Any(l => l.Kind == LayerKind.RangeEnd)) legend.Add(new("Range end", RangeEnd));
    return scene with { Shapes = shapes, Legend = legend };
  }

  /// <summary>The current solution in red; filled rectangles (jobs, items) keep their colors.</summary>
  private static Shape Red(Shape shape) => shape switch {
    PathShape p => p with { Color = Current, Thickness = Math.Max(2, p.Thickness) },
    LineShape l => l with { Color = Current, Thickness = Math.Max(2, l.Thickness) },
    MarkerShape m => m with { Color = Current },
    RectShape { Fill: null } r => r with { Stroke = Current, StrokeThickness = 2.5 },
    EllipseShape { Fill: null } e => e with { Stroke = Current },
    _ => shape
  };

  private static Shape? Restyle(Shape shape, Rgb color, double width) => shape switch {
    PathShape p => p with { Color = color, Thickness = width, Fill = null },
    LineShape l => l with { Color = color, Thickness = Math.Max(width, l.Thickness) },
    MarkerShape m => m with { Color = color, Label = null, Size = m.Size + (width > 2 ? width : 0) },
    RectShape r => r with { Fill = null, Stroke = color, StrokeThickness = Math.Max(1.5, width / 2), Label = null },
    EllipseShape e => e with { Fill = null, Stroke = color },
    _ => null
  };

  private static double R(double v) => Math.Round(v, 6);
  private static (double, double, double, double) Segment(Point2 a, Point2 b) =>
    (R(a.X), R(a.Y)).CompareTo((R(b.X), R(b.Y))) <= 0 ? (R(a.X), R(a.Y), R(b.X), R(b.Y)) : (R(b.X), R(b.Y), R(a.X), R(a.Y));

  private static IEnumerable<(Point2 A, Point2 B)> Segments(PathShape p) {
    for (int i = 1; i < p.Points.Count; i++) yield return (p.Points[i - 1], p.Points[i]);
    if (p.Closed && p.Points.Count > 2) yield return (p.Points[^1], p.Points[0]);
  }

  /// <summary>What the current solution has that the baseline does not: path segments, lines, rectangles, points.</summary>
  private static List<Shape> Changes(List<Shape> current, List<Shape> baseline) {
    var segments = new HashSet<(double, double, double, double)>();
    var boxes = new HashSet<(double, double, double, double)>();
    var points = new HashSet<(double, double)>();
    foreach (var shape in baseline) {
      switch (shape) {
        case PathShape p: foreach (var (a, b) in Segments(p)) segments.Add(Segment(a, b)); break;
        case LineShape l: segments.Add(Segment(l.From, l.To)); break;
        case RectShape r: boxes.Add((R(r.X), R(r.Y), R(r.Width), R(r.Height))); break;
        case EllipseShape e: boxes.Add((R(e.X), R(e.Y), R(e.Width), R(e.Height))); break;
        case MarkerShape m: points.Add((R(m.At.X), R(m.At.Y))); break;
      }
    }
    var changes = new List<Shape>();
    foreach (var shape in current) {
      switch (shape) {
        case PathShape p:
          foreach (var (a, b) in Segments(p))
            if (!segments.Contains(Segment(a, b))) changes.Add(new LineShape(a, b, Changed, 3.5));
          break;
        case LineShape l when !segments.Contains(Segment(l.From, l.To)):
          changes.Add(new LineShape(l.From, l.To, Changed, Math.Max(3.5, l.Thickness)));
          break;
        case RectShape r when !boxes.Contains((R(r.X), R(r.Y), R(r.Width), R(r.Height))):
          changes.Add(new RectShape(r.X, r.Y, r.Width, r.Height, null, Changed) { StrokeThickness = 3 });
          break;
        case EllipseShape e when !boxes.Contains((R(e.X), R(e.Y), R(e.Width), R(e.Height))):
          changes.Add(new EllipseShape(e.X, e.Y, e.Width, e.Height, null, Changed));
          break;
        case MarkerShape m when !points.Contains((R(m.At.X), R(m.At.Y))):
          changes.Add(m with { Color = Changed, Size = m.Size + 7, Label = null });
          break;
      }
    }
    return changes;
  }
}
