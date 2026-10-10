using HeuristicLab.Core;
using HeuristicLab.Data;
using HeuristicLab.Encodings.RealVectorEncoding;
using HeuristicLab.Problems.TestFunctions;
using HeuristicLab.Problems.TestFunctions.MultiObjective;

namespace HeuristicLab.Next.Runtime.Visuals;

/// <summary>
/// Test functions: two-dimensional ones as their landscape with the population (blue), the best
/// solution (green) and the best known solution (red), as HeuristicLab's test function view;
/// multi-objective ones as their Pareto front.
/// </summary>
internal static class TestFunctions {
  public static IReadOnlyList<Visual>? For(IItem item) => item switch {
    SingleObjectiveTestFunctionSolution s => [Solution(s)],
    ParetoFrontScatterPlot plot => Pareto(plot),
    _ => null
  };

  public static IReadOnlyList<Visual>? ForProblem(object problem) =>
    problem is SingleObjectiveTestFunctionProblem p && p.ProblemSize.Value == 2 && p.Evaluator != null
      ? [Landscape(Routes.BestKnownTitle(p), p.Evaluator, p.Bounds, [], null, p.BestKnownSolutionParameter.Value)]
      : null;

  private static Visual Solution(SingleObjectiveTestFunctionSolution s) {
    var best = s.BestRealVector;
    if (best is { Length: 2 } && s.Evaluator != null)
      return Landscape("Landscape", s.Evaluator, s.Bounds ?? s.Evaluator.Bounds, s.Population?.ToArray() ?? [], best, s.BestKnownRealVector);
    var components = best == null ? [] : best.Select((v, i) => ((double)i, v)).ToList();
    return new ChartVisual("Best solution", [new PlotSeries("Best solution", SeriesKind.Columns, components)], "Dimension", "Value") {
      Notes = ["The landscape is drawn for two-dimensional problems only"]
    };
  }

  private const int Resolution = 120;
  private static readonly Dictionary<string, Rgb[]> Landscapes = [];

  private static SceneVisual Landscape(string title, ISingleObjectiveTestFunctionProblemEvaluator evaluator, DoubleMatrix bounds,
                                       IReadOnlyList<RealVector> population, RealVector? best, RealVector? bestKnown) {
    double xMin = bounds[0, 0], xMax = bounds[0, 1], yMin = bounds[1 % bounds.Rows, 0], yMax = bounds[1 % bounds.Rows, 1];
    var shapes = new List<Shape> { new RasterShape(xMin, yMin, xMax - xMin, yMax - yMin, Resolution, Resolution, Pixels(evaluator, xMin, xMax, yMin, yMax)) };
    shapes.AddRange(population.Where(v => v.Length == 2).Select(v => (Shape)new MarkerShape(new Point2(v[0], v[1]), Rgb.Blue, 7) { IsSolution = true }));
    if (bestKnown is { Length: 2 }) shapes.Add(new MarkerShape(new Point2(bestKnown[0], bestKnown[1]), Rgb.Red, 11, MarkerKind.Diamond));
    if (best is { Length: 2 }) shapes.Add(new MarkerShape(new Point2(best[0], best[1]), Rgb.Green, 11, MarkerKind.Circle) { IsSolution = true });
    return new SceneVisual(title, shapes) {
      Axes = true, XAxisTitle = "x₁", YAxisTitle = "x₂",
      Legend = [new("Population", Rgb.Blue), new("Best", Rgb.Green), new("Best known", Rgb.Red)],
      Notes = [$"{evaluator.ItemName}: dark blue = low, dark red = high (by rank)"]
    };
  }

  /// <summary>Function values on a grid, colored by rank (so steep functions still show their structure); row 0 at the bottom.</summary>
  private static Rgb[] Pixels(ISingleObjectiveTestFunctionProblemEvaluator evaluator, double xMin, double xMax, double yMin, double yMax) {
    string key = $"{evaluator.GetType().FullName}|{xMin}|{xMax}|{yMin}|{yMax}";
    lock (Landscapes) if (Landscapes.TryGetValue(key, out var cached)) return cached;
    var values = new double[Resolution * Resolution];
    for (int r = 0; r < Resolution; r++)
      for (int c = 0; c < Resolution; c++) {
        double x = xMin + (c + 0.5) / Resolution * (xMax - xMin), y = yMax - (r + 0.5) / Resolution * (yMax - yMin);
        values[r * Resolution + c] = evaluator.Evaluate2D(x, y);
      }
    var order = Enumerable.Range(0, values.Length).Where(i => double.IsFinite(values[i])).OrderBy(i => values[i]).ToArray();
    var pixels = Enumerable.Repeat(Rgb.Gray, values.Length).ToArray();
    for (int rank = 0; rank < order.Length; rank++) pixels[order[rank]] = Rgb.Ramp((double)rank / Math.Max(1, order.Length - 1));
    lock (Landscapes) {
      if (Landscapes.Count > 16) Landscapes.Clear();
      Landscapes[key] = pixels;
    }
    return pixels;
  }

  /// <summary>The found front against the true Pareto front (first two objectives).</summary>
  private static IReadOnlyList<Visual> Pareto(ParetoFrontScatterPlot plot) {
    var series = new List<PlotSeries>();
    if (plot.ParetoFront is { Length: > 0 } front && front[0].Length >= 2)
      series.Add(new PlotSeries("Optimal front", SeriesKind.Points, front.Select(q => (q[0], q[1])).OrderBy(p => p.Item1).ToList(), Rgb.Gray));
    if (plot.Qualities is { Length: > 0 } qualities && qualities[0].Length >= 2)
      series.Add(new PlotSeries("Found front", SeriesKind.Points, qualities.Select(q => (q[0], q[1])).ToList(), Rgb.Blue));
    return [new ChartVisual("Pareto front", series, "Objective 1", "Objective 2") {
      Notes = plot.Objectives > 2 ? [$"First two of {plot.Objectives} objectives"] : []
    }];
  }
}
