using HeuristicLab.Core;
using HeuristicLab.Optimization;

namespace HeuristicLab.Next.Runtime.Visuals;

/// <summary>
/// What front ends draw for HeuristicLab items: results (solutions, tables, trees, packings,
/// schedules, ...) and problems (instance data and best known solutions). Mirrors the default
/// views of HeuristicLab's WinForms plugins. Items are read live: while an algorithm runs, the
/// caller tolerates InvalidOperationException/ArgumentException from concurrent changes and
/// simply asks again.
/// </summary>
public static class Visualizations {
  /// <summary>Pictures of a result value, most informative first; empty when there is nothing to draw.</summary>
  public static IReadOnlyList<Visual> For(IItem? item, string name = "") {
    if (item == null) return [];
    return Charts.For(item)
      ?? Routes.For(item)
      ?? Packing.For(item)
      ?? Schedules.For(item)
      ?? TestFunctions.For(item)
      ?? DataAnalysis.For(name, item)
      ?? Programs.For(item)
      ?? GenericTour(item)
      ?? [];
  }

  /// <summary>Pictures of a problem's instance: locations, best known solutions, landscapes, data.</summary>
  public static IReadOnlyList<Visual> ForProblem(IProblem? problem) {
    if (problem == null) return [];
    return Routes.ForProblem(problem)
      ?? Schedules.ForProblem(problem)
      ?? TestFunctions.ForProblem(problem)
      ?? DataAnalysis.ForProblem(problem)
      ?? Programs.ForProblem(problem)
      ?? [];
  }

  /// <summary>
  /// Pictures of a raw solution as an algorithm holds it (a permutation, vector, tree, ...), drawn
  /// with its problem's data (coordinates, distances, world, decoder, ...).
  /// </summary>
  public static IReadOnlyList<Visual> ForSolution(IProblem? problem, IItem solution, double? quality = null) =>
    RawSolutions.For(problem, solution, quality);

  /// <summary>Other items with coordinates and a permutation through them.</summary>
  private static IReadOnlyList<Visual>? GenericTour(IItem item) {
    if (ItemInspector.TourOf(item) is not { } tour) return null;
    var shapes = new List<Shape> { new PathShape(tour.Select(p => new Point2(p.X, p.Y)).ToList(), Rgb.Palette(0)) };
    shapes.AddRange(ItemInspector.PointsOf(item)!.Select(p => (Shape)new MarkerShape(new Point2(p.X, p.Y), Rgb.Palette(1), 5, MarkerKind.Square)));
    return [new SceneVisual("Tour", shapes)];
  }
}
