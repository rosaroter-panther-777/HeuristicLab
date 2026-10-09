using HeuristicLab.Cli;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Next.Runtime.Tests;

[TestClass]
public class SweepTests {
  private static string LegacyGaTsp => Path.Combine(AppContext.BaseDirectory, "TestData", "GA_TSP.hl");

  [TestMethod]
  public void ConfigurationsAreTheFullFactorial() {
    var configs = Sweeps.Configurations(["PopulationSize=10,20,30", "Selector=TournamentSelector,ProportionalSelector"]);
    Assert.AreEqual(6, configs.Count);
    CollectionAssert.AreEqual(new[] { "PopulationSize=10", "Selector=TournamentSelector" }, configs[0].ToArray());
    CollectionAssert.AreEqual(new[] { "PopulationSize=30", "Selector=ProportionalSelector" }, configs[^1].ToArray());
    Assert.ThrowsExactly<ArgumentException>(() => Sweeps.Configurations(["PopulationSize"]));
  }

  [TestMethod]
  public async Task SweepUsesTheSameSeedsAndLabelsEveryRun() {
    var ga = Documents.Load(LegacyGaTsp) as HeuristicLab.Algorithms.GeneticAlgorithm.GeneticAlgorithm;
    ga!.MaximumGenerations.Value = 20;
    var results = await Sweeps.RunAsync(ga, ["PopulationSize=20,40"], repetitions: 3, baseSeed: 7, parallelism: 3);

    Assert.AreEqual(2, results.Count);
    foreach (var r in results) CollectionAssert.AreEqual(new int?[] { 7, 8, 9 }, r.Batch.Reports.Select(x => x.RequestedSeed).ToArray());
    var run = results[1].Batch.Reports[0];
    Assert.AreEqual("PopulationSize=40", run.Labels!["config"]);
    Assert.AreEqual("40", run.Labels["PopulationSize"]);
    Assert.AreEqual(40, run.Runs.Single().Parameters["PopulationSize"].Value);
  }

  [TestMethod]
  public async Task InvalidSweepIsRejectedBeforeAnyRun() {
    var ga = Documents.Load(LegacyGaTsp) as HeuristicLab.Optimization.IOptimizer;
    var e = await Assert.ThrowsExactlyAsync<ArgumentException>(() => Sweeps.RunAsync(ga!, ["PopulationSize=20,lots"], 2));
    StringAssert.Contains(e.Message, "lots");
  }

  [TestMethod]
  public async Task CliSweepAndWalkForward() {
    var dir = Path.Combine(Path.GetTempPath(), $"hl-cli-sweep-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      async Task<(int, string)> Hl(params string[] args) {
        var o = new StringWriter(); var e = new StringWriter();
        int exit = await CliApp.RunAsync(args, o, e);
        return (exit, o + e.ToString());
      }
      var store = Path.Combine(dir, "sweep");
      var (sweepExit, sweepOut) = await Hl("run", LegacyGaTsp, "--set", "MaximumGenerations=10", "--sweep", "PopulationSize=10,20",
        "--repeat", "2", "--seed", "1", "--quiet", "--store", store);
      Assert.AreEqual(CliApp.ExitCompleted, sweepExit, sweepOut);
      StringAssert.Contains(sweepOut, "PopulationSize=20");
      var (summaryExit, summaryOut) = await Hl("store", "summary", store, "--by", "label:config");
      Assert.AreEqual(CliApp.ExitCompleted, summaryExit);
      StringAssert.Contains(summaryOut, "PopulationSize=10");

      var csv = Path.Combine(dir, "data.csv");
      File.WriteAllLines(csv, new[] { "x,y" }.Concat(Enumerable.Range(0, 120).Select(i => FormattableString.Invariant($"{i % 9},{2 * (i % 9) + 1}"))));
      var lr = Path.Combine(dir, "lr.hl");
      var (newExit, newOut) = await Hl("new", "Linear Regression (LR)", "--problem", "Regression Problem", "--data", csv, "--target", "y", "--out", lr);
      Assert.AreEqual(CliApp.ExitCompleted, newExit, newOut);
      var (wfExit, wfOut) = await Hl("walkforward", lr, "--train", "40", "--test", "20", "--expanding");
      Assert.AreEqual(CliApp.ExitCompleted, wfExit, wfOut);
      StringAssert.Contains(wfOut, "4 folds");
      StringAssert.Contains(wfOut, "train 0..79, test 80..99");
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
