using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;

namespace HeuristicLab.Studio.Controls;

/// <summary>
/// One of the pictograms in Assets/Pictograms (SVG files of single-color paths), drawn to fit.
/// Paths without a fill use Foreground; a path's own fill (e.g. the saved/unsaved symbols) is kept.
/// A pictogram with an animated rotation (Algorithm_status_working) turns as in the SVG: quarter
/// turns, eased, 1.2 s per full turn. The SVG files are read as they are, so they can be replaced.
/// </summary>
public sealed class Pictogram : Control {
  public static readonly StyledProperty<string?> SymbolProperty = AvaloniaProperty.Register<Pictogram, string?>(nameof(Symbol));
  public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<Pictogram, IBrush?>(nameof(Foreground), Brushes.Black);

  static Pictogram() {
    AffectsRender<Pictogram>(SymbolProperty, ForegroundProperty);
  }

  /// <summary>File name without ".svg", e.g. "experiment_logo".</summary>
  public string? Symbol {
    get => GetValue(SymbolProperty);
    set => SetValue(SymbolProperty, value);
  }

  public IBrush? Foreground {
    get => GetValue(ForegroundProperty);
    set => SetValue(ForegroundProperty, value);
  }

  /// <summary>Path data (with its fill rule) and its own fill color, if any.</summary>
  public sealed record PathPart(string Data, Color? Fill);
  public sealed record Drawing(IReadOnlyList<PathPart> Parts, Rect ViewBox, double SpinSeconds);

  private static readonly ConcurrentDictionary<string, Drawing?> Cache = new();
  // geometries and brushes belong to the UI thread that made them
  [ThreadStatic] private static Dictionary<string, (Geometry Geometry, IBrush? Fill)[]>? shapes;

  private static (Geometry Geometry, IBrush? Fill)[] Shapes(string symbol, Drawing drawing) {
    shapes ??= [];
    if (!shapes.TryGetValue(symbol, out var built))
      shapes[symbol] = built = drawing.Parts.Select(p => (Geometry.Parse(p.Data), p.Fill is { } c ? (IBrush?)new ImmutableSolidColorBrush(c) : null)).ToArray();
    return built;
  }

  /// <summary>The parsed pictogram, or null if there is no such file.</summary>
  public static Drawing? Load(string symbol) => Cache.GetOrAdd(symbol, s => {
    var uri = new Uri($"avares://HeuristicLab.Studio/Assets/Pictograms/{s}.svg");
    if (!AssetLoader.Exists(uri)) return null;
    using var stream = AssetLoader.Open(uri);
    return Parse(XDocument.Load(stream));
  });

  public static Drawing Parse(XDocument svg) {
    var root = svg.Root!;
    var box = ((string?)root.Attribute("viewBox"))?.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
      .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
    var viewBox = box is { Length: 4 } ? new Rect(box[0], box[1], box[2], box[3])
      : new Rect(0, 0, Number(root.Attribute("width")) ?? 24, Number(root.Attribute("height")) ?? 24);
    var parts = new List<PathPart>();
    foreach (var path in root.Descendants().Where(e => e.Name.LocalName == "path")) {
      if ((string?)path.Attribute("d") is not { } data) continue;
      // SVG fills nonzero unless told otherwise (inherited); Avalonia's path data needs it explicitly
      string? rule = path.AncestorsAndSelf().Select(e => (string?)e.Attribute("fill-rule")).FirstOrDefault(r => r != null);
      string? fill = path.AncestorsAndSelf().Select(e => (string?)e.Attribute("fill")).FirstOrDefault(f => f != null);
      parts.Add(new PathPart((rule == "evenodd" ? "F0 " : "F1 ") + data,
        fill is null or "currentColor" ? null : fill == "none" ? Colors.Transparent : Color.Parse(fill)));
    }
    var spin = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "animateTransform" && (string?)e.Attribute("type") == "rotate");
    double seconds = spin != null && ((string?)spin.Attribute("dur"))?.TrimEnd('s') is { } dur
      && double.TryParse(dur, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    return new Drawing(parts, viewBox, seconds);
  }

  private static double? Number(XAttribute? a) =>
    a != null && double.TryParse(a.Value.TrimEnd('p', 'x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

  private readonly Stopwatch clock = Stopwatch.StartNew();
  private DispatcherTimer? animation;

  protected override Size MeasureOverride(Size availableSize) => new(
    double.IsInfinity(availableSize.Width) ? 16 : availableSize.Width,
    double.IsInfinity(availableSize.Height) ? 16 : availableSize.Height);

  protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
    base.OnPropertyChanged(change);
    if (change.Property == SymbolProperty) UpdateAnimation();
  }

  protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
    base.OnAttachedToVisualTree(e);
    UpdateAnimation();
  }

  protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
    base.OnDetachedFromVisualTree(e);
    animation?.Stop();
  }

  /// <summary>Whether the shown pictogram turns.</summary>
  public bool IsSpinning => animation?.IsEnabled == true;

  private void UpdateAnimation() {
    bool spins = Symbol != null && Load(Symbol)?.SpinSeconds > 0 && VisualRoot != null;
    if (spins) {
      animation ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => InvalidateVisual());
      animation.Start();
    } else animation?.Stop();
  }

  /// <summary>Angle of a turning pictogram: counterclockwise quarter turns, each eased in and out (as the SVG's keySplines).</summary>
  public static double Angle(double seconds, double period) {
    double quarters = seconds / period * 4;
    double whole = Math.Floor(quarters), t = quarters - whole;
    double eased = t * t * (3 - 2 * t);
    return -90 * ((whole % 4) + eased);
  }

  public override void Render(DrawingContext context) {
    if (Symbol == null || Load(Symbol) is not { } drawing || Bounds.Width <= 0 || Bounds.Height <= 0) return;
    var box = drawing.ViewBox;
    double scale = Math.Min(Bounds.Width / box.Width, Bounds.Height / box.Height);
    double dx = (Bounds.Width - box.Width * scale) / 2, dy = (Bounds.Height - box.Height * scale) / 2;
    var transform = Matrix.CreateTranslation(-box.X, -box.Y) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy);
    if (drawing.SpinSeconds > 0) {
      var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
      double radians = Angle(clock.Elapsed.TotalSeconds, drawing.SpinSeconds) * Math.PI / 180;
      transform *= Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateRotation(radians) * Matrix.CreateTranslation(center.X, center.Y);
    }
    using (context.PushTransform(transform))
      foreach (var (geometry, fill) in Shapes(Symbol, drawing)) context.DrawGeometry(fill ?? Foreground, null, geometry);
  }
}
