using System.Globalization;
using HeuristicLab.Algorithms.GeneticAlgorithm;
using HeuristicLab.Data;
using HeuristicLab.Optimization;
using HeuristicLab.Problems.DataAnalysis;
using HeuristicLab.Problems.TravelingSalesman;
using HeuristicLab.Selection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Next.Runtime.Tests;

[TestClass]
public class ResearchWorkflowTests {
  private static string LegacyGaTsp => Path.Combine(AppContext.BaseDirectory, "TestData", "GA_TSP.hl");

  private static GeneticAlgorithm ShortGaTsp(int generations = 100) {
    var ga = (GeneticAlgorithm)Documents.Load(LegacyGaTsp);
    ga.MaximumGenerations.Value = generations;
    return ga;
  }

  [TestMethod]
  public void TspInstancesCanBeListedAndLoaded() {
    var tsp = new TravelingSalesmanProblem();
    var instances = ProblemInstances.For(tsp);
    Assert.IsTrue(instances.Any(i => i.Name == "berlin52"), "TSPLIB berlin52 expected");
    Assert.IsFalse(instances.Any(i => i.Provider.Contains("PTSP")), "PTSP instances only for PTSP problems");

    var loaded = ProblemInstances.Load(tsp, "berlin52");
    Assert.AreEqual("berlin52", loaded.Name);
    Assert.AreEqual(52, tsp.Coordinates.Rows);
    Assert.AreEqual(7542.0, tsp.BestKnownQuality.Value);
  }

  [TestMethod]
  public async Task AlgorithmFromCatalogWithInstanceAndParameters() {
    var ga = (GeneticAlgorithm)Catalog.Find(Catalog.Algorithms(), nameof(GeneticAlgorithm))!.CreateInstance();
    var tsp = (TravelingSalesmanProblem)Catalog.Find(Catalog.Problems(), nameof(TravelingSalesmanProblem))!.CreateInstance();
    ProblemInstances.Load(tsp, "berlin52");
    ga.Problem = tsp;
    ParameterEditor.Apply(ga, ["PopulationSize=50", "MaximumGenerations=30", "Selector=TournamentSelector", "MutationProbability=0.1"]);

    Assert.AreEqual(50, ga.PopulationSize.Value);
    Assert.IsInstanceOfType<TournamentSelector>(ga.Selector);
    var report = await OptimizerRunner.RunAsync(ga, new RunOptions { Seed = 3 });
    Assert.AreEqual(RunOutcome.Completed, report.Outcome, report.Error);
    Assert.AreEqual(30, report.Runs.Single().Results["Generations"].Value);
  }

  [TestMethod]
  public void InvalidParameterAssignmentsExplainThemselves() {
    var ga = new GeneticAlgorithm();
    var unknown = Assert.ThrowsExactly<ArgumentException>(() => ParameterEditor.Set(ga, "PopSize", "10"));
    StringAssert.Contains(unknown.Message, "PopulationSize");
    var badValue = Assert.ThrowsExactly<ArgumentException>(() => ParameterEditor.Set(ga, "PopulationSize", "many"));
    StringAssert.Contains(badValue.Message, "many");
    var badChoice = Assert.ThrowsExactly<ArgumentException>(() => ParameterEditor.Set(ga, "Selector", "NoSuchSelector"));
    StringAssert.Contains(badChoice.Message, "TournamentSelector");
  }

  [TestMethod]
  public async Task CsvImportIntoRegressionProblem() {
    // y = 2*x1 - 3*x2 + 1 exactly: linear regression must fit it perfectly
    var csv = Path.Combine(Path.GetTempPath(), $"hl-csv-{Guid.NewGuid():N}.csv");
    var rows = Enumerable.Range(0, 60).Select(i => (x1: i * 0.5, x2: Math.Sin(i) * 3));
    File.WriteAllLines(csv, new[] { "x1,x2,y" }.Concat(rows.Select(r =>
      string.Create(CultureInfo.InvariantCulture, $"{r.x1},{r.x2},{2 * r.x1 - 3 * r.x2 + 1}"))));
    try {
      var lr = (IAlgorithm)Catalog.Find(Catalog.Algorithms(), "Linear Regression (LR)")!.CreateInstance();
      var problem = new RegressionProblem();
      ProblemInstances.ImportCsv(problem, csv, target: "y", trainingPercent: 70);
      Assert.AreEqual("y", problem.ProblemData.TargetVariable);
      Assert.AreEqual(42, problem.ProblemData.TrainingPartition.End);
      lr.Problem = problem;

      var report = await OptimizerRunner.RunAsync(lr);
      Assert.AreEqual(RunOutcome.Completed, report.Outcome, report.Error);
      var solution = (IRegressionSolution)lr.Results["Linear regression solution"].Value;
      Assert.AreEqual(1.0, solution.TrainingRSquared, 1e-9);
      Assert.AreEqual(1.0, solution.TestRSquared, 1e-9);
    } finally {
      File.Delete(csv);
    }
  }

  [TestMethod]
  public async Task BatchIsReproducibleAndParallelEqualsSequential() {
    var template = ShortGaTsp();
    var sequential = await BatchRunner.RepeatAsync(BatchRunner.Copies(template), 4, baseSeed: 10, parallelism: 1);
    var parallel = await BatchRunner.RepeatAsync(BatchRunner.Copies(template), 4, baseSeed: 10, parallelism: 4);

    Assert.AreEqual(4, sequential.Completed);
    CollectionAssert.AreEqual(new int?[] { 10, 11, 12, 13 }, sequential.Reports.Select(r => r.RequestedSeed).ToList());
    CollectionAssert.AreEqual(Best(sequential), Best(parallel), "parallel runs must give the same results");
    var stats = sequential.Summary["CurrentBestQuality"];
    Assert.AreEqual(4, stats.Count);
    Assert.IsTrue(stats.Min <= stats.Median && stats.Median <= stats.Max);

    static List<double> Best(BatchReport b) => b.Reports.Select(r => (double)r.Runs.Single().Results["CurrentBestQuality"].Value!).ToList();
  }

  [TestMethod]
  public async Task UnseededBatchRecordsItsBaseSeed() {
    var batch = await BatchRunner.RepeatAsync(() => ShortGaTsp(20), 2);
    Assert.AreEqual(batch.BaseSeed, batch.Reports[0].RequestedSeed);
    Assert.AreEqual(batch.BaseSeed + 1, batch.Reports[1].RequestedSeed);
  }

  [TestMethod]
  public async Task StoreKeepsReportsAndExportsCsv() {
    var dir = Path.Combine(Path.GetTempPath(), $"hl-store-{Guid.NewGuid():N}");
    try {
      var store = new ResultStore(dir);
      var batch = await BatchRunner.RepeatAsync(() => ShortGaTsp(), 3, baseSeed: 0);
      foreach (var report in batch.Reports) store.Add(report);

      Assert.AreEqual(3, store.Reports().Count);
      var rows = store.Rows();
      Assert.AreEqual(3, rows.Count);
      Assert.AreEqual("0", rows.Single(r => r["seed"] == "0")["param:Seed"]);
      var best = rows.Select(r => double.Parse(r["result:CurrentBestQuality"], CultureInfo.InvariantCulture)).ToList();
      CollectionAssert.AreEqual(batch.Reports.Select(r => (double)r.Runs.Single().Results["CurrentBestQuality"].Value!).Order().ToList(), best.Order().ToList());

      var csv = new StringWriter();
      ResultStore.WriteCsv(rows, csv);
      var lines = csv.ToString().TrimEnd().Split('\n');
      Assert.AreEqual(4, lines.Length);
      StringAssert.StartsWith(lines[0], "file,startedAt,optimizer");
      StringAssert.Contains(lines[0], "result:CurrentBestQuality");
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }
}
