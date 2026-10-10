using System.Globalization;
using HeuristicLab.Core;
using HeuristicLab.Data;
using HeuristicLab.Encodings.BinaryVectorEncoding;
using HeuristicLab.Encodings.IntegerVectorEncoding;
using HeuristicLab.Encodings.PermutationEncoding;
using HeuristicLab.Encodings.RealVectorEncoding;
using HeuristicLab.Encodings.ScheduleEncoding;
using HeuristicLab.Encodings.ScheduleEncoding.JobSequenceMatrix;
using HeuristicLab.Encodings.SymbolicExpressionTreeEncoding;
using HeuristicLab.Optimization;
using HeuristicLab.Problems.Knapsack;
using HeuristicLab.Problems.LinearAssignment;
using HeuristicLab.Problems.Orienteering;
using HeuristicLab.Problems.PTSP;
using HeuristicLab.Problems.QuadraticAssignment;
using HeuristicLab.Problems.Scheduling;
using HeuristicLab.Problems.TestFunctions;
using HeuristicLab.Problems.TravelingSalesman;
using HeuristicLab.Problems.VehicleRouting;
using HeuristicLab.Problems.VehicleRouting.Interfaces;
using Ant = HeuristicLab.Problems.GeneticProgramming.ArtificialAnt;
using LawnMower = HeuristicLab.Problems.GeneticProgramming.LawnMower;

namespace HeuristicLab.Next.Runtime.Visuals;

/// <summary>
/// Raw solutions (what an algorithm keeps in its scopes: permutations, vectors, trees, schedules)
/// turned into the solution items of their problem, so they get the problem's picture.
/// </summary>
internal static class RawSolutions {
  public static IItem? AsResult(IProblem problem, IItem solution, double? quality) {
    var q = quality is double v ? new DoubleValue(v) : null;
    switch (problem, solution) {
      case (TravelingSalesmanProblem p, Permutation tour): return new PathTSPTour(p.Coordinates, tour, q);
      case (ProbabilisticTravelingSalesmanProblem p, Permutation tour): return new PathPTSPTour(p.Coordinates, p.Probabilities, tour, q);
      case (QuadraticAssignmentProblem p, Permutation assignment): return new QAPAssignment(p.Weights, assignment, q) { Distances = p.Distances };
      case (LinearAssignmentProblem p, Permutation assignment): return new LAPAssignment(p.Costs, p.RowNames, p.ColumnNames, assignment, q);
      case (KnapsackProblem p, BinaryVector packed): return new KnapsackSolution(packed, q, p.KnapsackCapacity, p.Weights, p.Values);
      case (OrienteeringProblem p, IntegerVector tour):
        return new OrienteeringSolution(tour, p.Coordinates, new IntValue(p.StartingPoint), new IntValue(p.TerminalPoint), p.Scores, q);
      case (VehicleRoutingProblem p, IVRPEncoding routes): return new VRPSolution(p.ProblemInstance, routes, q ?? new DoubleValue(double.NaN));
      case (SingleObjectiveTestFunctionProblem p, RealVector point):
        return new SingleObjectiveTestFunctionSolution(point, q, p.Evaluator) { Bounds = p.Bounds, BestKnownRealVector = p.BestKnownSolutionParameter.Value };
      case (Ant.Problem p, ISymbolicExpressionTree program): return new Ant.Solution(p.World, program, p.MaxTimeSteps.Value, quality ?? 0);
      case (LawnMower.Problem p, ISymbolicExpressionTree program):
        return new LawnMower.Solution(program, p.LawnLengthParameter.Value.Value, p.LawnWidthParameter.Value.Value, quality ?? 0);
      case (JobShopSchedulingProblem p, Schedule schedule): return schedule;
      case (JobShopSchedulingProblem p, IScheduleEncoding encoding):
        // decoders read the job data from their scope; the job sequence matrix decoder also takes it directly
        return p.ScheduleDecoder is ScheduleDecoder decoder ? Decode(decoder, encoding, p)
             : encoding is JSMEncoding ? Decode(new JSMDecoder(), encoding, p) : null;
      default: return Packing(problem, solution);
    }
  }

  /// <summary>
  /// Schedule decoders look up the job data, error policies and a random generator in their scope:
  /// run a copy (the algorithm may be using the original) in a scope that holds the problem's
  /// parameter values, as the algorithm's scope tree makes them visible.
  /// </summary>
  private static Schedule? Decode(ScheduleDecoder decoder, IScheduleEncoding encoding, JobShopSchedulingProblem problem) {
    var copy = (ScheduleDecoder)decoder.Clone();
    var scope = new Scope("Decoding");
    foreach (var parameter in ((IParameterizedItem)problem).Parameters.OfType<IValueParameter>().Where(p => p.Value != null))
      scope.Variables.Add(new Variable(parameter.Name, parameter.Value));
    if (!scope.Variables.ContainsKey("Random")) scope.Variables.Add(new Variable("Random", new HeuristicLab.Random.MersenneTwister(0)));
    scope.Variables.Add(new Variable(copy.ScheduleEncodingParameter.ActualName, encoding));
    copy.Execute(new HeuristicLab.Core.ExecutionContext(null, copy, scope), CancellationToken.None);
    return scope.Variables.TryGetValue(copy.ScheduleParameter.ActualName, out var schedule) ? schedule.Value as Schedule : null;
  }

  /// <summary>Bin packing: the problem's decoder places the items (2D and 3D, permutation or integer vector).</summary>
  private static IItem? Packing(IProblem problem, IItem solution) {
    if (!problem.GetType().Namespace!.StartsWith("HeuristicLab.Problems.BinPacking", StringComparison.Ordinal)) return null;
    dynamic p = problem;
    object decoder = p.Decoder;
    var decode = decoder.GetType().GetMethods().FirstOrDefault(m => m.Name == "Decode" && m.GetParameters()[0].ParameterType.IsInstanceOfType(solution));
    if (decode == null) return null;
    var arguments = decode.GetParameters().Select<System.Reflection.ParameterInfo, object?>((parameter, i) => i == 0 ? solution
      : parameter.ParameterType == typeof(bool) ? (bool)p.UseStackingConstraints
      : parameter.Name == "binShape" ? p.BinShape : (object)p.Items).ToArray();
    return decode.Invoke(decoder, arguments) as IItem;
  }

  /// <summary>Pictures of a raw solution: its problem's pictures, else trees and plain values.</summary>
  public static IReadOnlyList<Visual> For(IProblem? problem, IItem solution, double? quality) {
    var result = problem == null ? null : AsResult(problem, solution, quality);
    var visuals = Visualizations.For(result ?? solution);
    if (visuals.Count > 0) return visuals;
    string title = quality is double v ? $"Solution (quality {v.ToString("G8", CultureInfo.InvariantCulture)})" : "Solution";
    if (solution is IStringConvertibleArray array && array.Length > 0 &&
        Enumerable.Range(0, array.Length).All(i => double.TryParse(array.GetValue(i), NumberStyles.Float, CultureInfo.InvariantCulture, out _) || bool.TryParse(array.GetValue(i), out _)))
      return [new ChartVisual(title, [new PlotSeries("Value", SeriesKind.Columns, Enumerable.Range(0, array.Length)
        .Select(i => ((double)i, bool.TryParse(array.GetValue(i), out var b) ? (b ? 1.0 : 0.0) : double.Parse(array.GetValue(i), CultureInfo.InvariantCulture))).ToList())],
        "Position", "Value")];
    return [new TextVisual(title, solution.ToString() ?? "")];
  }
}
