using HeuristicLab.Algorithms.GeneticAlgorithm;
using HeuristicLab.Common;
using HeuristicLab.Core;
using HeuristicLab.Optimization;
using HeuristicLab.Problems.TravelingSalesman;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Next.Runtime.Tests;

[TestClass]
public class RuntimeTests {
  private static string LegacyGaTsp => Path.Combine(AppContext.BaseDirectory, "TestData", "GA_TSP.hl");

  [TestMethod]
  public async Task LegacyFileRunsSeededWithLegacyResults() {
    // GA_TSP.hl was saved by legacy HeuristicLab; with seed 0 it must reproduce the values the
    // legacy test suite (GeneticAlgorithmTest) asserts.
    var optimizer = (IOptimizer)Documents.Load(LegacyGaTsp);
    var report = await OptimizerRunner.RunAsync(optimizer, new RunOptions { Seed = 0 }, LegacyGaTsp);

    Assert.AreEqual(RunOutcome.Completed, report.Outcome, report.Error);
    var run = report.Runs.Single();
    Assert.AreEqual(12332.0, run.Results["CurrentBestQuality"].Value);
    Assert.AreEqual(13123.2, (double)run.Results["CurrentAverageQuality"].Value!, 1e-9);
    Assert.AreEqual(14538.0, run.Results["CurrentWorstQuality"].Value);
    Assert.AreEqual(0, run.Parameters["Seed"].Value);
    Assert.AreEqual(false, run.Parameters["SetSeedRandomly"].Value);
  }

  [TestMethod]
  public async Task ReportCarriesProvenanceAndSerializesToJson() {
    var optimizer = (IOptimizer)Documents.Load(LegacyGaTsp);
    var report = await OptimizerRunner.RunAsync(optimizer, new RunOptions { Seed = 0 }, LegacyGaTsp);

    StringAssert.StartsWith(report.Provenance.Runtime, ".NET ");
    Assert.AreEqual(64, report.Provenance.InputSha256!.Length);
    Assert.AreEqual(0, report.RequestedSeed);

    var json = ReportJson.Serialize(report);
    StringAssert.Contains(json, "\"CurrentBestQuality\"");  // dictionary keys keep their names
    StringAssert.Contains(json, "\"outcome\": \"Completed\"");
    // quality history table converted to plain rows
    var history = report.Runs.Single().Results.Values.Where(v => v.Type == "DataTable").ToList();
    Assert.IsTrue(history.Count > 0, "expected at least one DataTable result");
  }

  [TestMethod]
  public async Task CancellationStopsTheRunAndKeepsPartialResults() {
    var ga = (GeneticAlgorithm)Documents.Load(LegacyGaTsp);
    ga.MaximumGenerations.Value = 1_000_000;
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    var report = await OptimizerRunner.RunAsync(ga, new RunOptions { Seed = 1 }, cancellationToken: cts.Token);

    Assert.AreEqual(RunOutcome.Stopped, report.Outcome);
    Assert.AreEqual(ExecutionState.Stopped, ga.ExecutionState);
    Assert.IsTrue(report.Runs.Single().Results.ContainsKey("CurrentBestQuality"));
  }

  [TestMethod]
  public async Task ProgressIsReported() {
    var ga = (GeneticAlgorithm)Documents.Load(LegacyGaTsp);
    var snapshots = new List<RunProgress>();
    var progress = new SynchronousProgress<RunProgress>(snapshots.Add);
    await OptimizerRunner.RunAsync(ga, new RunOptions { Seed = 0, ProgressInterval = TimeSpan.FromMilliseconds(50) }, progress: progress);

    Assert.IsTrue(snapshots.Count > 0);
    Assert.IsTrue(snapshots[^1].Values.ContainsKey("CurrentBestQuality"));
  }

  [TestMethod]
  public void CatalogListsAlgorithmsAndProblems() {
    var algorithms = Catalog.Algorithms();
    var problems = Catalog.Problems();
    Assert.IsNotNull(Catalog.Find(algorithms, "Genetic Algorithm (GA)") ?? Catalog.Find(algorithms, nameof(GeneticAlgorithm)));
    Assert.IsNotNull(Catalog.Find(problems, nameof(TravelingSalesmanProblem)));
    Assert.IsInstanceOfType<GeneticAlgorithm>(Catalog.Find(algorithms, nameof(GeneticAlgorithm))!.CreateInstance());
  }

  [TestMethod]
  public async Task SavedRunReloadsWithItsRuns() {
    var optimizer = (IOptimizer)Documents.Load(LegacyGaTsp);
    await OptimizerRunner.RunAsync(optimizer, new RunOptions { Seed = 0 });
    var path = Path.Combine(Path.GetTempPath(), $"hl-runtime-test-{Guid.NewGuid():N}.hl");
    try {
      Documents.Save((IStorableContent)optimizer, path);
      var reloaded = (IOptimizer)Documents.Load(path);
      Assert.AreEqual(optimizer.Runs.Count, reloaded.Runs.Count);
      Assert.AreEqual(12332.0, OptimizerRunner.ToRecord(reloaded.Runs.Last()).Results["CurrentBestQuality"].Value);
    } finally {
      File.Delete(path);
    }
  }

  /// <summary>Progress&lt;T&gt; posts to the thread pool; this one reports inline for deterministic tests.</summary>
  private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T> {
    private readonly object sync = new();
    public void Report(T value) { lock (sync) report(value); }
  }
}
