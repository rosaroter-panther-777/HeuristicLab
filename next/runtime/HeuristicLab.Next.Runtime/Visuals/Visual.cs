namespace HeuristicLab.Next.Runtime.Visuals;

/// <summary>An sRGB color with alpha.</summary>
public readonly record struct Rgb(byte R, byte G, byte B, byte A = 255) {
  // HeuristicLab's 3D packing colors, also distinct enough for tours, routes and series
  private static readonly Rgb[] Colors = [
    new(0x2E, 0x6F, 0xD8), new(0xE0, 0x7B, 0x24), new(0x3A, 0xA6, 0x5B), new(0xC2, 0x3B, 0x4A), new(0x7B, 0x52, 0xC4),
    new(0x72, 0x9F, 0xCF), new(0xB1, 0x6D, 0x01), new(0x4E, 0x8A, 0x06), new(0xAD, 0x7F, 0xA8), new(0xED, 0xD4, 0x30),
    new(0x55, 0x57, 0x53), new(0xEF, 0x59, 0x59), new(0x63, 0xC2, 0x16), new(0x29, 0x50, 0xCF)
  ];

  public static Rgb Palette(int index) => Colors[((index % Colors.Length) + Colors.Length) % Colors.Length];
  public static readonly Rgb Black = new(0x20, 0x20, 0x20);
  public static readonly Rgb Gray = new(0x90, 0x90, 0x90);
  public static readonly Rgb Red = new(0xC2, 0x3B, 0x4A);
  public static readonly Rgb Blue = new(0x2E, 0x6F, 0xD8);
  public static readonly Rgb Green = new(0x3A, 0xA6, 0x5B);
  public static readonly Rgb Brown = new(0x8B, 0x4A, 0x1C);

  public Rgb WithAlpha(byte alpha) => this with { A = alpha };

  /// <summary>Color ramp for intensities in [0, 1]: dark blue over teal and yellow to dark red.</summary>
  public static Rgb Ramp(double t) {
    if (!double.IsFinite(t)) return Gray;
    t = Math.Clamp(t, 0, 1);
    Rgb[] stops = [new(0x1F, 0x2A, 0x6B), new(0x22, 0x8B, 0x9A), new(0x9C, 0xD0, 0x5A), new(0xF2, 0xD3, 0x3C), new(0xB2, 0x23, 0x2B)];
    double position = t * (stops.Length - 1);
    int i = Math.Min((int)position, stops.Length - 2);
    double f = position - i;
    byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * f);
    return new Rgb(Mix(stops[i].R, stops[i + 1].R), Mix(stops[i].G, stops[i + 1].G), Mix(stops[i].B, stops[i + 1].B));
  }
}

/// <summary>
/// A picture of an item, independent of any GUI toolkit: front ends render the few kinds below.
/// Notes are short facts shown with it (e.g. "Food eaten: 67 of 89").
/// </summary>
public abstract record Visual(string Title) {
  public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>How a chart series is drawn (HeuristicLab's DataRowChartType).</summary>
public enum SeriesKind { Line, StepLine, Points, Columns, Bars, Histogram }

/// <param name="Width">Column or histogram bin width in x units (0: derived from the spacing).</param>
public sealed record PlotSeries(string Name, SeriesKind Kind, IReadOnlyList<(double X, double Y)> Points,
                                Rgb? Color = null, bool SecondYAxis = false, double Width = 0);

/// <param name="Markers">Labelled vertical lines, e.g. the start of the test partition.</param>
public sealed record ChartVisual(string Title, IReadOnlyList<PlotSeries> Series, string XAxisTitle = "", string YAxisTitle = "",
                                 string SecondYAxisTitle = "") : Visual(Title) {
  public IReadOnlyList<(double X, string Label)> Markers { get; init; } = [];
  public bool LogX { get; init; }
  public bool LogY { get; init; }
}

public readonly record struct Point2(double X, double Y);

public enum MarkerKind { Circle, Square, Diamond }

public enum TextAnchor { Center, Left, Right }

public abstract record Shape;

public sealed record LineShape(Point2 From, Point2 To, Rgb Color, double Thickness = 1) : Shape;

/// <summary>Polyline or polygon; thickness in pixels.</summary>
public sealed record PathShape(IReadOnlyList<Point2> Points, Rgb Color, double Thickness = 1.5, bool Closed = false, Rgb? Fill = null) : Shape;

/// <summary>A point marker whose size (pixels) does not change with zoom.</summary>
public sealed record MarkerShape(Point2 At, Rgb Color, double Size = 6, MarkerKind Kind = MarkerKind.Circle, string? Label = null) : Shape;

/// <summary>Rectangle in scene coordinates; the label is drawn centered if it fits.</summary>
public sealed record RectShape(double X, double Y, double Width, double Height, Rgb? Fill, Rgb? Stroke = null, string? Label = null) : Shape;

/// <summary>Circle or ellipse in scene coordinates.</summary>
public sealed record EllipseShape(double X, double Y, double Width, double Height, Rgb? Fill, Rgb? Stroke = null) : Shape;

/// <summary>Text at a scene position, in a fixed pixel size.</summary>
public sealed record TextShape(Point2 At, string Text, Rgb Color, TextAnchor Anchor = TextAnchor.Center, double Size = 11) : Shape;

/// <summary>
/// A grid of colors (row-major, Rows x Columns) covering a rectangle, e.g. a function landscape or
/// heat map. Row 0 is drawn at the top, whichever way the scene's y axis points.
/// </summary>
public sealed record RasterShape(double X, double Y, double Width, double Height, int Rows, int Columns, Rgb[] Pixels) : Shape;

public sealed record LegendEntry(string Label, Rgb Color);

/// <summary>
/// 2D drawing in its own coordinates. YUp: y grows upwards (maps, function spaces); otherwise
/// downwards (grids, trees, Gantt charts). Uniform: x and y use the same scale (geometry); else
/// the drawing is stretched to the available space (time axes). Axes draws a scale around it.
/// </summary>
public sealed record SceneVisual(string Title, IReadOnlyList<Shape> Shapes, bool YUp = true, bool Uniform = true) : Visual(Title) {
  public bool Axes { get; init; }
  /// <summary>With Axes: whether the horizontal axis has a scale (a knapsack's width means nothing).</summary>
  public bool XAxisScale { get; init; } = true;
  public string XAxisTitle { get; init; } = "";
  public string YAxisTitle { get; init; } = "";
  public IReadOnlyList<LegendEntry> Legend { get; init; } = [];
}

/// <summary>Axis-aligned box: position (x right, y up, z towards the viewer) and size.</summary>
public sealed record Box3(double X, double Y, double Z, double Width, double Height, double Depth, Rgb Color, string? Label = null);

/// <summary>Boxes in a container (3D bin packing), viewed from a rotatable camera.</summary>
public sealed record BoxesVisual(string Title, Box3 Container, IReadOnlyList<Box3> Boxes) : Visual(Title);

/// <summary>Preformatted text, e.g. a model as formula or program code.</summary>
public sealed record TextVisual(string Title, string Text) : Visual(Title);
