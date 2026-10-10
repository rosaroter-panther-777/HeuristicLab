using System.Globalization;
using HeuristicLab.Analysis;
using HeuristicLab.Core;
using HeuristicLab.Data;
using HeuristicLab.Encodings.BinaryVectorEncoding;
using HeuristicLab.Encodings.IntegerVectorEncoding;
using HeuristicLab.Encodings.PermutationEncoding;
using HeuristicLab.Problems.Knapsack;
using HeuristicLab.Problems.LinearAssignment;
using HeuristicLab.Problems.Orienteering;
using HeuristicLab.Problems.PTSP;
using HeuristicLab.Problems.QuadraticAssignment;
using HeuristicLab.Problems.TravelingSalesman;
using HeuristicLab.Problems.VehicleRouting;
using HeuristicLab.Problems.VehicleRouting.Interfaces;
using HeuristicLab.Problems.VehicleRouting.Variants;

namespace HeuristicLab.Next.Runtime.Visuals;

/// <summary>Tours, routes and assignments, drawn like HeuristicLab's problem views.</summary>
internal static class Routes {
  private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

  public static IReadOnlyList<Visual>? For(IItem item) => item switch {
    PathTSPTour tour => Single(Tour("Tour", tour.Coordinates, tour.Permutation, null)),
    PathPTSPTour tour => Single(Tour("Tour", tour.Coordinates, tour.Permutation, tour.Probabilities)),
    VRPSolution solution => Single(Vrp("Routes", solution.ProblemInstance, solution.Solution)),
    OrienteeringSolution s => Single(Orienteering("Tour", s.Coordinates, s.Scores, s.IntegerVector,
      s.StartingPoint?.Value ?? 0, s.TerminalPoint?.Value ?? 0, s.Penalty?.Value > 0)),
    QAPAssignment a => Single(Qap("Assignment", a.Distances, a.Weights, a.Assignment)),
    LAPAssignment a => Single(Lap("Assignment", a.Costs, a.RowNames, a.ColumnNames, a.Assignment)),
    KnapsackSolution s => Single(Knapsack("Knapsack", s.BinaryVector, s.Capacity?.Value ?? 0, s.Weights, s.Values)),
    _ => null
  };

  public static IReadOnlyList<Visual>? ForProblem(object problem) => problem switch {
    TravelingSalesmanProblem p => Single(Tour(BestKnownTitle(p), p.Coordinates, p.BestKnownSolution, null)),
    ProbabilisticTravelingSalesmanProblem p => Single(Tour(BestKnownTitle(p), p.Coordinates, p.BestKnownSolution, p.Probabilities)),
    VehicleRoutingProblem p when p.ProblemInstance != null =>
      Single(Vrp(p.BestKnownSolution != null ? BestKnownTitle(p) : "Locations", p.ProblemInstance, p.BestKnownSolution?.Solution)),
    OrienteeringProblem p => Single(Orienteering(BestKnownTitle(p), p.Coordinates, p.Scores, p.BestKnownSolution,
      p.StartingPoint, p.TerminalPoint, false)),
    QuadraticAssignmentProblem p => Single(Qap(BestKnownTitle(p), p.Distances, p.Weights, p.BestKnownSolution)),
    LinearAssignmentProblem p => Single(Lap(BestKnownTitle(p), p.Costs, p.RowNames, p.ColumnNames, p.BestKnownSolution)),
    KnapsackProblem p when p.BestKnownSolution != null =>
      Single(Knapsack(BestKnownTitle(p), p.BestKnownSolution, p.KnapsackCapacity.Value, p.Weights, p.Values)),
    _ => null
  };

  private static IReadOnlyList<Visual>? Single(Visual? visual) => visual == null ? null : [visual];

  internal static string BestKnownTitle(IParameterizedItem problem) =>
    problem.Parameters.TryGetValue("BestKnownQuality", out var parameter) && parameter is IValueParameter { Value: DoubleValue quality }
      ? $"Best known solution (quality {quality.Value.ToString("G10", Invariant)})"
      : "Best known solution";

  private static Point2[]? Points(DoubleMatrix? coordinates) =>
    coordinates is { Rows: > 0, Columns: >= 2 }
      ? Enumerable.Range(0, coordinates.Rows).Select(r => new Point2(coordinates[r, 0], coordinates[r, 1])).ToArray()
      : null;

  /// <summary>Locations and, if given, the closed tour through them; PTSP: marker size by visiting probability.</summary>
  private static Visual? Tour(string title, DoubleMatrix? coordinates, Permutation? tour, DoubleArray? probabilities) {
    var points = Points(coordinates);
    if (points == null) return null;
    var shapes = new List<Shape>();
    if (tour != null && tour.Length == points.Length)
      shapes.Add(new PathShape(tour.Select(i => points[i]).ToList(), Rgb.Palette(0), 1.5, Closed: true));
    for (int i = 0; i < points.Length; i++) {
      double size = probabilities != null && i < probabilities.Length ? 3 + 7 * Math.Clamp(probabilities[i], 0, 1) : 5;
      shapes.Add(new MarkerShape(points[i], Rgb.Palette(1), size, MarkerKind.Square));
    }
    var notes = new List<string> { $"{points.Length} locations" };
    if (probabilities != null) notes.Add("Marker size: probability that the location must be visited");
    return new SceneVisual(tour == null ? "Locations" : title, shapes) { Notes = notes };
  }

  /// <summary>One colored route per vehicle, from and back to its depot (HeuristicLab's VRP view).</summary>
  private static Visual? Vrp(string title, IVRPProblemInstance? instance, IVRPEncoding? solution) {
    if (instance == null || Points(instance.Coordinates) is not { } points) return null;
    var depots = instance is IMultiDepotProblemInstance multi ? multi.Depots.Value : 1;
    var shapes = new List<Shape>();
    var notes = new List<string>();
    if (solution != null) {
      var tours = solution.GetTours();
      notes.Add($"{tours.Count(t => t.Stops.Count > 0)} routes");
      for (int t = 0; t < tours.Count; t++) {
        var tour = tours[t];
        if (tour.Stops.Count == 0) continue;
        int depot = 0;
        if (instance is IMultiDepotProblemInstance md) {
          int vehicle = solution.GetVehicleAssignment(solution.GetTourIndex(tour));
          depot = md.VehicleDepotAssignment[vehicle];
        }
        var route = new List<Point2> { points[depot] };
        route.AddRange(tour.Stops.Select(stop => Location(instance, stop)));
        shapes.Add(new PathShape(route, Rgb.Palette(t), 1.6, Closed: true));
      }
    }
    for (int city = 1; city <= instance.Cities.Value; city++)
      shapes.Add(new MarkerShape(Location(instance, city), Rgb.Black, 5, MarkerKind.Square));
    for (int d = 0; d < depots && d < points.Length; d++)
      shapes.Add(new MarkerShape(points[d], Rgb.Blue, 11, MarkerKind.Circle, depots > 1 ? $"Depot {d + 1}" : "Depot"));
    notes.Insert(0, $"{instance.Cities.Value} customers, {depots} depot{(depots > 1 ? "s" : "")}");
    return new SceneVisual(title, shapes) { Notes = notes };
  }

  private static Point2 Location(IVRPProblemInstance instance, int city) {
    var c = instance.GetCoordinates(city);
    return new Point2(c[0], c[1]);
  }

  /// <summary>Points sized by score, the tour from begin to end (red when it exceeds the maximum distance).</summary>
  private static Visual? Orienteering(string title, DoubleMatrix? coordinates, DoubleArray? scores, IntegerVector? tour,
                                      int start, int end, bool penalized) {
    var points = Points(coordinates);
    if (points == null) return null;
    var shapes = new List<Shape>();
    if (tour is { Length: > 1 })
      shapes.Add(new PathShape(tour.Where(i => i >= 0 && i < points.Length).Select(i => points[i]).ToList(),
        penalized ? Rgb.Red : Rgb.Black, 1.5));
    double min = scores is { Length: > 0 } ? scores.Min() : 0, max = scores is { Length: > 0 } ? scores.Max() : 0;
    for (int i = 0; i < points.Length; i++) {
      double score = scores != null && i < scores.Length ? scores[i] : 0;
      double size = max > min ? 3 + 8 * (score - min) / (max - min) : 6;
      shapes.Add(new MarkerShape(points[i], Rgb.Red, size, MarkerKind.Square));
    }
    if (start == end && start >= 0 && start < points.Length)
      shapes.Add(new MarkerShape(points[start], Rgb.Blue, 10, MarkerKind.Circle, "Begin and end"));
    else {
      if (start >= 0 && start < points.Length) shapes.Add(new MarkerShape(points[start], Rgb.Blue, 10, MarkerKind.Circle, "Begin"));
      if (end >= 0 && end < points.Length) shapes.Add(new MarkerShape(points[end], Rgb.Green, 10, MarkerKind.Circle, "End"));
    }
    var notes = new List<string> { "Marker size: score of the location" };
    if (tour != null) notes.Insert(0, $"Visits {tour.Length} of {points.Length} locations");
    if (penalized) notes.Add("Red: the tour exceeds the maximum distance");
    return new SceneVisual(title, shapes) { Notes = notes };
  }

  private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DoubleMatrix, Tuple<DoubleMatrix, double>> Layouts = new();

  /// <summary>
  /// Locations placed by multidimensional scaling of their distances; facilities drawn at their
  /// assigned locations, connected by lines as thick as their flow (HeuristicLab's QAP view).
  /// </summary>
  private static Visual? Qap(string title, DoubleMatrix? distances, DoubleMatrix? weights, Permutation? assignment) {
    if (distances is not { Rows: > 1 } || weights == null || distances.Rows != distances.Columns) return null;
    int n = distances.Rows;
    for (int i = 0; i < n; i++)
      for (int j = i + 1; j < n; j++)
        if (distances[i, j] != distances[j, i])
          return new TextVisual(title, "The distance matrix is not symmetric, so the locations cannot be placed in the plane.");
    var layout = Layouts.GetValue(distances, d => {
      var coordinates = MultidimensionalScaling.KruskalShepard(d);
      return Tuple.Create(coordinates, MultidimensionalScaling.CalculateNormalizedStress(d, coordinates));
    });
    var points = Points(layout.Item1)!;
    var shapes = new List<Shape>();
    var notes = new List<string> { $"Locations placed by multidimensional scaling (stress {layout.Item2.ToString("0.00", Invariant)})" };
    if (assignment != null && assignment.Length == n && weights.Rows == n) {
      double maxWeight = 0;
      for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) maxWeight = Math.Max(maxWeight, weights[i, j] + weights[j, i]);
      for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++) {
          double w = weights[i, j] + weights[j, i];
          if (w > 0 && maxWeight > 0)
            shapes.Add(new LineShape(points[assignment[i]], points[assignment[j]], Rgb.Blue.WithAlpha(170), Math.Ceiling(4 * w / maxWeight)));
        }
      for (int f = 0; f < n; f++)
        shapes.Add(new MarkerShape(points[assignment[f]], Rgb.Black, 7, MarkerKind.Square, f.ToString(Invariant)));
      notes.Add("Labels: facilities at their assigned locations; line width: flow between them");
    } else {
      for (int l = 0; l < n; l++) shapes.Add(new MarkerShape(points[l], Rgb.Black, 7, MarkerKind.Square, l.ToString(Invariant)));
      notes.Add("No assignment known");
    }
    return new SceneVisual(title, shapes) { Notes = notes };
  }

  /// <summary>The cost matrix as colors; assigned cells outlined.</summary>
  private static Visual? Lap(string title, DoubleMatrix? costs, StringArray? rowNames, StringArray? columnNames, Permutation? assignment) {
    if (costs is not { Rows: > 0, Columns: > 0 }) return null;
    int rows = costs.Rows, columns = costs.Columns;
    double min = double.MaxValue, max = double.MinValue;
    for (int r = 0; r < rows; r++) for (int c = 0; c < columns; c++) { min = Math.Min(min, costs[r, c]); max = Math.Max(max, costs[r, c]); }
    var pixels = new Rgb[rows * columns];
    for (int r = 0; r < rows; r++)
      for (int c = 0; c < columns; c++)
        pixels[r * columns + c] = Rgb.Ramp(max > min ? (costs[r, c] - min) / (max - min) : 0.5);
    var shapes = new List<Shape> { new RasterShape(0, 0, columns, rows, rows, columns, pixels) };
    if (assignment != null)
      for (int r = 0; r < Math.Min(rows, assignment.Length); r++)
        shapes.Add(new RectShape(assignment[r], r, 1, 1, null, Rgb.Black));
    if (rows <= 40)
      for (int r = 0; r < rows; r++)
        shapes.Add(new TextShape(new Point2(-0.2, r + 0.5), rowNames != null && r < rowNames.Length ? rowNames[r] : $"{r}", Rgb.Black, TextAnchor.Right));
    if (columns <= 40)
      for (int c = 0; c < columns; c++)
        shapes.Add(new TextShape(new Point2(c + 0.5, -0.15), columnNames != null && c < columnNames.Length ? columnNames[c] : $"{c}", Rgb.Black));
    var notes = new List<string> { $"Costs from {min:G4} (dark blue) to {max:G4} (dark red)" };
    notes.Add(assignment != null ? "Outlined: the assigned cell of each row" : "No assignment known");
    return new SceneVisual(title, shapes, YUp: false) { Notes = notes };
  }

  /// <summary>The knapsack (height = capacity) with the packed items stacked, height by weight, darker blue = less value.</summary>
  private static Visual? Knapsack(string title, BinaryVector? packed, double capacity, IntArray? weights, IntArray? values) {
    if (packed == null || weights == null || values == null || capacity <= 0) return null;
    var items = Enumerable.Range(0, Math.Min(packed.Length, weights.Length)).Where(i => packed[i]).OrderBy(i => values[i]).ToList();
    double maxValue = values.Length > 0 ? values.Max() : 1, used = items.Sum(i => (double)weights[i]);
    var shapes = new List<Shape> { new RectShape(0, 0, 1, capacity, null, Rgb.Black) };
    double y = 0;
    foreach (var i in items) {
      var color = new Rgb(0, (byte)(60 * values[i] / Math.Max(1, maxValue)), (byte)(Math.Round(255.0 * values[i] / Math.Max(1, maxValue))));
      shapes.Add(new RectShape(0.05, y, 0.9, weights[i], color, new Rgb(255, 255, 255), $"{i}: w {weights[i]}, v {values[i]}"));
      y += weights[i];
    }
    return new SceneVisual(title, shapes, YUp: true, Uniform: false) {
      Axes = true, XAxisScale = false, YAxisTitle = "Weight",
      Notes = [$"{items.Count} of {packed.Length} items packed", $"Weight {used.ToString(Invariant)} of capacity {capacity.ToString(Invariant)}",
               $"Total value {items.Sum(i => (double)values[i]).ToString(Invariant)}", "Brighter blue: more valuable item"]
    };
  }
}
