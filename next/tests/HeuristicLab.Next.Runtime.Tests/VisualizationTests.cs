using System.Collections.Concurrent;
using HeuristicLab.Core;
using HeuristicLab.Data;
using HeuristicLab.Next.Runtime.Visuals;
using HeuristicLab.Optimization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Next.Runtime.Tests;

[TestClass]
public class VisualizationTests {
  /// <summary>Problems that cannot run out of the box, each with its reason.</summary>
  private static readonly Dictionary<string, string> CannotRun = new() {
    ["HeuristicLab.Problems.GeneticProgramming.Robocode.Problem"] = "needs a Robocode installation",
    ["HeuristicLab.Problems.Programmable.SingleObjectiveProgrammableProblem"] = "its script is empty until the user writes one",
    ["HeuristicLab.Problems.Programmable.MultiObjectiveProgrammableProblem"] = "its script is empty until the user writes one",
    ["HeuristicLab.Optimization.UserDefinedProblem"] = "has no evaluator until the user chooses one",
    ["HeuristicLab.Algorithms.DataAnalysis.GaussianProcessCovarianceOptimizationProblem"] = "one evaluation fits a Gaussian process (minutes)",
  };

  // orienteering offers no crossover or mutation, only the neighborhoods of variable neighborhood search
  private static readonly string[] PreferredAlgorithms = ["GeneticAlgorithm", "NSGA2", "LinearRegression", "RandomForestClassification", "KMeansClustering"];
  private static readonly Dictionary<string, string> AlgorithmFor = new() {
    ["HeuristicLab.Problems.Orienteering.OrienteeringProblem"] = "VariableNeighborhoodSearch"
  };

  /// <summary>Runs a few generations of a fitting algorithm on the problem's first benchmark instance.</summary>
  private static IAlgorithm Run(string problemType, string? algorithm = null, string? instance = null) {
    var problem = (IProblem)Catalog.Find(Catalog.Problems(), problemType)!.CreateInstance();
    algorithm ??= AlgorithmFor.GetValueOrDefault(problemType);
    algorithm ??= Catalog.Algorithms().Where(a => !a.Type.Name.StartsWith("CrossValidation"))
      .Where(a => ((IAlgorithm)a.CreateInstance()).ProblemType.IsInstanceOfType(problem))
      .OrderBy(a => Array.IndexOf(PreferredAlgorithms, a.Type.Name) is var i && i >= 0 ? i : int.MaxValue)
      .Select(a => a.TypeName).First();
    instance ??= ProblemInstances.For(problem).FirstOrDefault()?.QualifiedName;
    var setup = Setups.Create(new SetupRequest(algorithm) { Problem = problemType, Instance = instance });
    if (setup.Algorithm.Parameters.ContainsKey("MaximumGenerations")) ParameterEditor.Apply(setup.Algorithm, ["MaximumGenerations=5"]);
    if (setup.Algorithm.Parameters.ContainsKey("MaximumIterations")) ParameterEditor.Apply(setup.Algorithm, ["MaximumIterations=20"]);
    var report = OptimizerRunner.RunAsync(setup.Algorithm, new RunOptions { Seed = 1, Timeout = TimeSpan.FromSeconds(20) }).GetAwaiter().GetResult();
    Assert.IsNull(report.Error, $"{problemType}: {report.Error}");
    return setup.Algorithm;
  }

  private static IItem Result(IAlgorithm algorithm, string name) => algorithm.Results[name].Value;

  /// <summary>Encodings and plain data have no picture of their own in HeuristicLab either; everything else must have one.</summary>
  private static bool NeedsPicture(IItem value) {
    var type = value.GetType();
    if (type.Namespace is "HeuristicLab.Data" or "HeuristicLab.Core") return false;
    string[] plainEncodings = ["BinaryVector", "RealVector", "IntegerVector", "Permutation", "LinearLinkage"];
    return !plainEncodings.Contains(type.Name);
  }

  [TestMethod]
  [Timeout(600_000)]
  public void EveryProblemsResultsAndInstanceCanBeDrawn() {
    var failures = new ConcurrentBag<string>();
    var problems = Catalog.Problems().Where(p => !CannotRun.ContainsKey(p.TypeName)).ToList();
    Parallel.ForEach(problems, new ParallelOptions { MaxDegreeOfParallelism = 4 }, entry => {
      try {
        var algorithm = Run(entry.TypeName);
        Visualizations.ForProblem(algorithm.Problem);
        foreach (var result in algorithm.Results) {
          var visuals = Visualizations.For(result.Value, result.Name);
          if (visuals.Count == 0 && NeedsPicture(result.Value))
            failures.Add($"{entry.Name}: result '{result.Name}' ({result.Value.GetType().Name}) has no visualization");
        }
      } catch (Exception e) {
        failures.Add($"{entry.Name}: {e.GetType().Name}: {e.Message}");
      }
    });
    Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures.Order()));
  }

  [TestMethod]
  public void AntTrailReplaysTheEvolvedProgram() {
    var ga = Run("HeuristicLab.Problems.GeneticProgramming.ArtificialAnt.Problem");
    var best = Result(ga, "Best Solution");
    var visuals = Visualizations.For(best);
    var trail = (SceneVisual)visuals[0];
    Assert.AreEqual("Ant trail", trail.Title);
    // the trail eats exactly as much food as the solution's quality says (the ant maximizes food eaten)
    var eaten = trail.Notes.Single(n => n.StartsWith("Food eaten"));
    double solutionQuality = (double)best.GetType().GetProperty("Quality")!.GetValue(best)!;
    StringAssert.StartsWith(eaten, $"Food eaten: {solutionQuality} of 89");
    Assert.IsTrue(trail.Shapes.OfType<EllipseShape>().Count() == 89, "food of the Santa Fe trail");
    Assert.IsTrue(trail.Shapes.OfType<RectShape>().Count(r => r.Fill == Rgb.Brown) > 1, "the ant's positions");
    Assert.IsInstanceOfType<SceneVisual>(visuals[1]);
    Assert.AreEqual("Tree", visuals[1].Title);

    var world = Visualizations.ForProblem(ga.Problem).Single();
    Assert.AreEqual("World", world.Title);
  }

  [TestMethod]
  public void TreeLengthsAreAHistogramOverThePopulation() {
    var ga = Run("HeuristicLab.Problems.GeneticProgramming.Boolean.EvenParityProblem");
    var chart = (ChartVisual)Visualizations.For(Result(ga, "Symbolic expression tree lengths")).Single();
    var histogram = chart.Series.Single();
    Assert.AreEqual(SeriesKind.Histogram, histogram.Kind);
    Assert.IsTrue(histogram.Width > 0);
    int populationSize = ((IntValue)ga.Parameters["PopulationSize"].ActualValue!).Value;
    Assert.AreEqual(populationSize, histogram.Points.Sum(p => p.Y), "every tree of the last generation is counted once");
    var lengths = (ChartVisual)Visualizations.For(Result(ga, "Symbolic expression tree length")).Single();
    Assert.IsTrue(lengths.Series.All(s => s.Kind == SeriesKind.Line));

    var tree = (SceneVisual)Visualizations.For(Result(ga, "Best Solution")).Single();
    Assert.IsTrue(tree.Shapes.OfType<RectShape>().Any(r => r.Label == "ProgramRootSymbol"));
  }

  [TestMethod]
  public void ThreeDimensionalPackingsAreBoxesInsideTheirBins() {
    var ga = Run("HeuristicLab.Problems.BinPacking3D.PermutationProblem");
    var bins = Visualizations.For(Result(ga, "Best Packing Solution")).Cast<BoxesVisual>().ToList();
    Assert.IsTrue(bins.Count >= 1);
    Assert.AreEqual(10, bins.Sum(b => b.Boxes.Count), "all ten items of the instance are packed");
    foreach (var bin in bins)
      foreach (var box in bin.Boxes) {
        Assert.IsTrue(box.X >= 0 && box.X + box.Width <= bin.Container.Width, $"{bin.Title} item {box.Label} within width");
        Assert.IsTrue(box.Y >= 0 && box.Y + box.Height <= bin.Container.Height, $"{bin.Title} item {box.Label} within height");
        Assert.IsTrue(box.Z >= 0 && box.Z + box.Depth <= bin.Container.Depth, $"{bin.Title} item {box.Label} within depth");
      }

    var ga2 = Run("HeuristicLab.Problems.BinPacking2D.PermutationProblem");
    var plans = Visualizations.For(Result(ga2, "Best Packing Solution")).Cast<SceneVisual>().ToList();
    Assert.AreEqual(20, plans.Sum(p => p.Shapes.OfType<RectShape>().Count() - 1), "twenty items, plus one outline per bin");
  }

  [TestMethod]
  public void SolutionsOfEachProblemFamilyGetTheirPicture() {
    void Expect<T>(string problem, string result, string title) where T : Visual {
      var ga = Run(problem);
      var visual = Visualizations.For(Result(ga, result), result).First();
      Assert.IsInstanceOfType<T>(visual, problem);
      Assert.AreEqual(title, visual.Title, problem);
    }
    Expect<SceneVisual>("HeuristicLab.Problems.VehicleRouting.VehicleRoutingProblem", "Best VRP Solution", "Routes");
    Expect<SceneVisual>("HeuristicLab.Problems.Scheduling.JobShopSchedulingProblem", "Best Scheduling Solution", "Gantt chart");
    Expect<SceneVisual>("HeuristicLab.Problems.TestFunctions.SingleObjectiveTestFunctionProblem", "Best Solution", "Landscape");
    Expect<ChartVisual>("HeuristicLab.Problems.TestFunctions.MultiObjective.MultiObjectiveTestFunctionProblem", "Scatterplot", "Pareto front");
    Expect<SceneVisual>("HeuristicLab.Problems.Knapsack.KnapsackProblem", "Best Knapsack Solution", "Knapsack");
    Expect<SceneVisual>("HeuristicLab.Problems.LinearAssignment.LinearAssignmentProblem", "Best LAP Solution", "Assignment");
    Expect<SceneVisual>("HeuristicLab.Problems.GeneticProgramming.LawnMower.Problem", "Best Solution", "Lawn");
    Expect<ChartVisual>("HeuristicLab.Problems.DataAnalysis.Symbolic.Regression.SymbolicRegressionSingleObjectiveProblem", "Best training solution", "Line chart");
    Expect<ChartVisual>("HeuristicLab.Problems.DataAnalysis.Symbolic.Classification.SymbolicClassificationSingleObjectiveProblem", "Best training solution", "Estimated class values");
  }

  [TestMethod]
  public void QapAssignmentPlacesFacilitiesAtTheirLocations() {
    var ga = Run("HeuristicLab.Problems.QuadraticAssignment.QuadraticAssignmentProblem", instance: "QAPLIB/nug12");
    var scene = (SceneVisual)Visualizations.For(Result(ga, "Best QAP Solution")).Single();
    Assert.AreEqual(12, scene.Shapes.OfType<MarkerShape>().Count());
    Assert.IsTrue(scene.Shapes.OfType<LineShape>().Any(), "flows between facilities");
    StringAssert.Contains(scene.Notes[0], "multidimensional scaling");
  }

  [TestMethod]
  public void ProblemTabsShowInstancesAndBestKnownSolutions() {
    IReadOnlyList<Visual> For(string problem, string? instance = null) {
      var setup = Setups.Create(new SetupRequest("GeneticAlgorithm") { Problem = problem, Instance = instance });
      return Visualizations.ForProblem(setup.Algorithm.Problem);
    }
    var tsp = (SceneVisual)For("TravelingSalesmanProblem", "TSPLIB (symmetric TSP)/ch130").Single();
    Assert.AreEqual("Best known solution (quality 6110)", tsp.Title);
    Assert.AreEqual(130, tsp.Shapes.OfType<PathShape>().Single().Points.Count);
    Assert.IsInstanceOfType<SceneVisual>(For("HeuristicLab.Problems.VehicleRouting.VehicleRoutingProblem").Single());
    Assert.IsInstanceOfType<SceneVisual>(For("HeuristicLab.Problems.TestFunctions.SingleObjectiveTestFunctionProblem").Single());
    Assert.IsInstanceOfType<SceneVisual>(For("HeuristicLab.Problems.Orienteering.OrienteeringProblem").Single());
    var jssp = For("HeuristicLab.Problems.Scheduling.JobShopSchedulingProblem", "ORLIB JSSP/ft06");
    Assert.IsTrue(jssp.Count <= 1);
    var data = (ChartVisual)Visualizations.ForProblem(Setups.Create(new SetupRequest("LinearRegression") {
      Problem = "RegressionProblem", Instance = ProblemInstances.For(new HeuristicLab.Problems.DataAnalysis.RegressionProblem())[0].QualifiedName
    }).Algorithm.Problem).Single();
    Assert.AreEqual("Target variable", data.Title);
    Assert.IsTrue(data.Markers.Any(m => m.Label == "test"));
  }
}
