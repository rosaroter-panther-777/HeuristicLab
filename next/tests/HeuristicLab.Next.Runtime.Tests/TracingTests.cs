using HeuristicLab.Data;
using HeuristicLab.Encodings.PermutationEncoding;
using HeuristicLab.Next.Runtime.Tracing;
using HeuristicLab.Optimization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Next.Runtime.Tests;

[TestClass]
public class TracingTests {
  private static IAlgorithm Tsp(string algorithm, params string[] settings) =>
    Setups.Create(new SetupRequest(algorithm) { Problem = "TravelingSalesmanProblem", Instance = "ch130", Settings = settings }).Algorithm;

  private static Trace Trace(IAlgorithm algorithm, int storageLimit = 200_000) {
    var (trace, run) = DetailedRun.Start(algorithm, new DetailedRunOptions { Seed = 7, StorageLimit = storageLimit });
    var report = run.GetAwaiter().GetResult();
    Assert.IsNull(report.Error, report.Error);
    Assert.IsTrue(trace.IsFinished);
    return trace;
  }

  private static double Result(Trace trace, string name) => ((DoubleValue)trace.Algorithm.Results[name].Value).Value;

  [TestMethod]
  public void GeneticAlgorithmRecordsEverySolutionWithItsParents() {
    var ga = Tsp("GeneticAlgorithm", "PopulationSize=50", "MaximumGenerations=10");
    var trace = Trace(ga);
    Assert.AreEqual(0, ga.Runs.Count, "the original algorithm is not run");
    Assert.AreEqual("TSPTour", trace.SolutionName);

    var iterations = trace.Iterations();
    CollectionAssert.AreEqual(Enumerable.Range(0, 11).ToList(), iterations.Select(i => i.Number).ToList());
    var initial = trace.Solutions(0, 0);
    Assert.AreEqual(50, initial.Count, "the initial population");
    Assert.IsTrue(initial.All(s => s.Parents.Count == 0 && s.Quality.HasValue && s.Solution is Permutation { Length: 130 }));

    var offspring = trace.Solutions(1, 1);
    var crossovers = offspring.Where(s => s.Parents.Count == 2).ToList();
    Assert.AreEqual(50, crossovers.Count, "HeuristicLab's GA makes a full population of children (the elite is put back afterwards)");
    Assert.IsTrue(crossovers.All(c => c.Parents.All(p => p < c.Id)), "parents exist before their children");
    // mutations change a child: one parent, the crossover child of the same step sequence
    Assert.IsTrue(offspring.Where(s => s.Parents.Count == 1).All(m => trace.Solution(m.Parents[0]).Parents.Count == 2));

    // what the trace saw is what HeuristicLab's analyzer reports
    Assert.AreEqual(Result(trace, "BestQuality"), iterations[^1].BestSoFar);
    Assert.IsTrue(iterations.All(i => i.Duration >= 0 && i.Start >= 0));
    Assert.IsTrue(iterations.Zip(iterations.Skip(1)).All(p => p.First.Start <= p.Second.Start), "time increases");
  }

  [TestMethod]
  public void TabuSearchRecordsItsTrajectory() {
    var ts = Tsp("TabuSearch", "MaximumIterations=30");
    var trace = Trace(ts);
    var solutions = trace.Solutions();
    Assert.AreEqual(1, solutions.Count(s => s.Parents.Count == 0), "one start solution");
    Assert.IsTrue(solutions.Skip(1).All(s => s.Parents.Single() == s.Id - 1), "each move changes the previous solution");
    Assert.IsTrue(solutions.Skip(1).All(s => s.Operator.Contains("MoveMaker")), string.Join(", ", solutions.Select(s => s.Operator).Distinct()));
    Assert.AreEqual(31, trace.Iterations().Count, "initialization and 30 iterations");
    Assert.IsTrue(trace.Iterations().Skip(1).All(i => i.StepCount == 1), "one accepted move per iteration");
    Assert.AreEqual(Result(trace, "BestQuality"), trace.Iterations()[^1].BestSoFar);
  }

  [TestMethod]
  public void StorageLimitKeepsCountingWithoutCopies() {
    var trace = Trace(Tsp("GeneticAlgorithm", "PopulationSize=20", "MaximumGenerations=3"), storageLimit: 25);
    var solutions = trace.Solutions();
    Assert.IsTrue(solutions.Count > 25);
    Assert.IsTrue(solutions.Take(25).All(s => s.Solution != null));
    Assert.IsTrue(solutions.Skip(25).All(s => s.Solution == null && s.Quality.HasValue || s.Solution == null));
  }

  [TestMethod]
  public void AlgorithmsWithoutOperatorGraphCannotBeTraced() {
    var lr = Setups.Create(new SetupRequest("LinearRegression") { Problem = "RegressionProblem" }).Algorithm;
    Assert.IsFalse(DetailedRun.CanTrace(lr));
    Assert.ThrowsException<NotSupportedException>(() => DetailedRun.Start(lr));
  }
}

[TestClass]
public class TracingSampleTests {
  public TestContext TestContext { get; set; } = null!;

  /// <summary>A short detailed run of every algorithm sample: steps are recorded and recorded solutions are drawn.</summary>
  [TestMethod]
  [Timeout(600_000)]
  public void EverySampleCanBeTracedAndItsSolutionsDrawn() {
    var report = new System.Collections.Concurrent.ConcurrentBag<string>();
    var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
    var ids = Samples.Ids.Where(id => Samples.GroupOf(id) != Samples.Scripts && id != "SGP_Robocode").ToList();
    Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = 4 }, id => {
      try {
        var algorithm = (IAlgorithm)Samples.Load(id);
        if (!DetailedRun.CanTrace(algorithm)) {
          if (id != "GPR") failures.Add($"{id}: {DetailedRun.WhyNot(algorithm)}");
          return;
        }
        // structure templates optimize parameters in every evaluation: creating the population takes a while
        var seconds = id == "GP_Structure_Template_Regression" ? 20 : 4;
        var (trace, run) = DetailedRun.Start(algorithm, new DetailedRunOptions { Seed = 1, Timeout = TimeSpan.FromSeconds(seconds) });
        var result = run.GetAwaiter().GetResult();
        if (result.Outcome == RunOutcome.Failed) { failures.Add($"{id}: {result.Error?.Split('\n')[0]}"); return; }
        var solutions = trace.Solutions();
        if (solutions.Count == 0) { failures.Add($"{id}: no solutions recorded (solution variable {trace.SolutionName ?? "not found"})"); return; }
        var last = solutions.Last(s => s.Solution != null);
        var visual = HeuristicLab.Next.Runtime.Visuals.Visualizations.ForSolution(trace.Problem, last.Solution!, last.Quality)[0];
        report.Add($"{id}: {solutions.Count} solutions in {trace.Iterations().Count} iterations, {trace.SolutionName}, drawn as {visual.GetType().Name} '{visual.Title}', e.g. {last.Operator}");
      } catch (Exception e) {
        failures.Add($"{id}: {e}");
      }
    });
    foreach (var line in report.Order()) TestContext.WriteLine(line);
    Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures.Order()));
  }
}
