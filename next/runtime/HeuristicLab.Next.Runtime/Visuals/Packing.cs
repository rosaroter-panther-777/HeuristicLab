using System.Globalization;
using HeuristicLab.Core;
using Plan2D = HeuristicLab.Problems.BinPacking2D.Solution;
using Plan3D = HeuristicLab.Problems.BinPacking3D.Solution;

namespace HeuristicLab.Next.Runtime.Visuals;

/// <summary>
/// Bin packings, one picture per bin: 2D as rectangles, 3D as boxes in a rotatable view. Items are
/// colored by material, as HeuristicLab's packing views do; when all items share one material
/// (most benchmark instances), by item instead, so neighbors can be told apart.
/// </summary>
internal static class Packing {
  public static IReadOnlyList<Visual>? For(IItem item) => item switch {
    Plan2D plan => Plan2(plan),
    Plan3D plan => Plan3(plan),
    _ => null
  };

  private static Func<int, int, Rgb> Colors(IEnumerable<int> materials) =>
    materials.Distinct().Take(2).Count() > 1 ? (_, material) => Rgb.Palette(material) : (id, _) => Rgb.Palette(id);

  private static string Density(double density) => (density * 100).ToString("0.#", CultureInfo.InvariantCulture) + " %";

  private static IReadOnlyList<Visual> Plan2(Plan2D plan) {
    var bins = plan.Bins.ToArray();
    var color = Colors(bins.SelectMany(b => b.Items.Values.ToArray()).Select(i => i.Material));
    return bins.Select((bin, b) => {
      var shape = bin.BinShape;
      var shapes = new List<Shape> { new RectShape(0, 0, shape.Width, shape.Height, new Rgb(0xF4, 0xF4, 0xF4), Rgb.Black) };
      foreach (var (id, position) in bin.Positions.ToArray()) {
        var packed = bin.Items[id];
        double w = position.Rotated ? packed.Height : packed.Width, h = position.Rotated ? packed.Width : packed.Height;
        shapes.Add(new RectShape(position.X, position.Y, w, h, color(id, packed.Material).WithAlpha(220), Rgb.Black, id.ToString(CultureInfo.InvariantCulture)));
      }
      return (Visual)new SceneVisual($"Bin {b + 1} of {bins.Length}", shapes) {
        Axes = true,
        Notes = [$"{bin.Positions.Count} items, {shape.Width} × {shape.Height}", $"Packing density {Density(bin.PackingDensity)}"]
      };
    }).ToList();
  }

  private static IReadOnlyList<Visual> Plan3(Plan3D plan) {
    var bins = plan.Bins.ToArray();
    var color = Colors(bins.SelectMany(b => b.Items.Values.ToArray()).Select(i => i.Material));
    return bins.Select((bin, b) => {
      var shape = bin.BinShape;
      var boxes = bin.Positions.ToArray().Select(p => {
        var packed = bin.Items[p.Key];
        var position = p.Value;
        double w = position.Rotated ? packed.Depth : packed.Width, d = position.Rotated ? packed.Width : packed.Depth;
        return new Box3(position.X, position.Y, position.Z, w, packed.Height, d, color(p.Key, packed.Material), p.Key.ToString(CultureInfo.InvariantCulture));
      }).ToList();
      return (Visual)new BoxesVisual($"Bin {b + 1} of {bins.Length}", new Box3(0, 0, 0, shape.Width, shape.Height, shape.Depth, Rgb.Gray), boxes) {
        Notes = [$"{boxes.Count} items, {shape.Width} × {shape.Height} × {shape.Depth}", $"Packing density {Density(bin.PackingDensity)}"]
      };
    }).ToList();
  }
}
