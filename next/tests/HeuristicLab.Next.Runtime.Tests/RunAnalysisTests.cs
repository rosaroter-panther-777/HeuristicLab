using HeuristicLab.Next.Runtime.Runs;
using HeuristicLab.Next.Runtime.Visuals;
using HeuristicLab.Optimization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Next.Runtime.Tests;

[TestClass]
public class RunAnalysisTests {
  private static Experiment experiment = null!;

  /// <summary>An experiment comparing two population sizes of a GA on ch130, four runs each.</summary>
  [ClassInitialize]
  public static void Run(TestContext _) {
    experiment = new Experiment { Name = "Population sizes" };
    foreach (var size in new[] { 20, 50 }) {
      var ga = Setups.Create(new SetupRequest("GeneticAlgorithm") {
        Problem = "TravelingSalesmanProblem", Instance = "ch130", Settings = [$"PopulationSize={size}", "MaximumGenerations=30"]
      }).Algorithm;
      experiment.Optimizers.Add(new BatchRun($"GA {size}") { Optimizer = ga, Repetitions = 4 });
    }
    var report = OptimizerRunner.RunAsync(experiment).GetAwaiter().GetResult();
    Assert.IsNull(report.Error, report.Error);
  }

  private static RunTable Table() => RunTable.FromRuns(experiment.Runs.Select(r => new SourcedRun(experiment.Name, "", r)));

  [TestMethod]
  public void RunsBecomeATableWithParametersAndResults() {
    var table = Table();
    Assert.AreEqual(8, table.Rows.Count);
    Assert.IsTrue(table.Column("param:PopulationSize")!.IsNumeric);
    Assert.IsTrue(table.Column("result:BestQuality")!.IsNumeric);
    Assert.IsFalse(table.Column("param:Algorithm Name")!.IsNumeric);
    Assert.AreEqual("PopulationSize (parameter)", table.Column("param:PopulationSize")!.ToString());
    Assert.AreEqual(4, table.Where([new RunFilter("param:PopulationSize", "=", "50")]).Rows.Count);
    Assert.AreEqual(8, table.Where([new RunFilter("param:Algorithm Name", "contains", "genetic")]).Rows.Count);

    var csv = new StringWriter();
    table.WriteCsv(csv, ["param:PopulationSize", "result:BestQuality"]);
    var lines = csv.ToString().Trim().Split('\n');
    Assert.AreEqual(9, lines.Length);
    Assert.AreEqual("PopulationSize (parameter),BestQuality (result)", lines[0].Trim());
  }

  [TestMethod]
  public void GroupsCompareWithHeuristicLabsTests() {
    var table = Table();
    var groups = RunGrouping.Group(table, ["param:PopulationSize"]);
    CollectionAssert.AreEqual(new[] { "PopulationSize 20", "PopulationSize 50" }, groups.Select(g => g.Name).ToList());
    var comparison = RunStatistics.Compare(groups, "result:BestQuality");
    Assert.AreEqual(2, comparison.Groups.Count);
    Assert.IsTrue(comparison.Groups.All(g => g.Count == 4 && g.Min <= g.Q1 && g.Q1 <= g.Median && g.Median <= g.Q3 && g.Q3 <= g.Max));
    Assert.IsNotNull(comparison.KruskalWallisP);
    var pair = comparison.Pairs.Single();
    Assert.IsTrue(pair.MannWhitneyP is >= 0 and <= 1 && pair.AdjustedP >= pair.MannWhitneyP);

    // several columns at once, numbers in bins
    Assert.AreEqual(2, RunGrouping.Group(table, ["param:Algorithm Name", "param:PopulationSize"]).Count);
    var binned = RunGrouping.Group(table, ["result:BestQuality"], bins: 3);
    Assert.IsTrue(binned.Count <= 3 && binned.All(g => g.Name.StartsWith("BestQuality [")));
    Assert.AreEqual("All runs", RunGrouping.Group(table, []).Single().Name);
  }

  [TestMethod]
  public void ChartsShowEveryGroup() {
    var table = Table();
    var groups = RunGrouping.Group(table, ["param:PopulationSize"]);
    var scatter = RunCharts.Scatter(table, groups, RunCharts.RunIndex, "result:BestQuality");
    Assert.AreEqual(8, scatter.Series.Sum(s => s.Points.Count));
    var box = (SceneVisual)RunCharts.BoxPlot(table, groups, "result:BestQuality");
    CollectionAssert.AreEquivalent(groups.Select(g => g.Name).ToList(), box.Legend.Select(l => l.Label).ToList());
    Assert.AreEqual(2, box.Shapes.OfType<RectShape>().Count(), "one box per group");
    var histogram = RunCharts.Histogram(table, groups, "result:BestQuality");
    Assert.AreEqual(8, histogram.Series.Sum(s => s.Points.Sum(p => p.Y)));
    var cumulative = RunCharts.Cumulative(table, groups, "result:BestQuality");
    Assert.IsTrue(cumulative.Series.All(s => s.Points[^1].Y == 1));

    var (tableName, rows) = table.Curves().Single(c => c.Table == "Qualities");
    CollectionAssert.Contains(rows.ToList(), "BestQuality");
    var curves = RunCharts.Curves(groups, tableName, "BestQuality");
    Assert.AreEqual(4, curves.Series.Count, "runs and mean per group");
    var runsOf20 = curves.Series[0];
    Assert.AreEqual(3, runsOf20.Points.Count(p => double.IsNaN(p.Y)), "four runs, three gaps");
  }

  [TestMethod]
  public void ResultsFoldersJoinTheSameTable() {
    var store = RunTable.FromStore("folder", [
      new Dictionary<string, string> { ["optimizer"] = "GA", ["result:BestQuality"] = "6200.5", ["param:PopulationSize"] = "100" },
      new Dictionary<string, string> { ["optimizer"] = "GA", ["result:BestQuality"] = "6300", ["param:PopulationSize"] = "100" }
    ]);
    var combined = RunTable.Combine([Table(), store]);
    Assert.AreEqual(10, combined.Rows.Count);
    Assert.IsTrue(combined.Column("result:BestQuality")!.IsNumeric);
    Assert.AreEqual(2, RunGrouping.Group(combined, ["source"]).Count);
  }
}
