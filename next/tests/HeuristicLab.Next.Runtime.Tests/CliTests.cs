using System.Text.Json;
using HeuristicLab.Cli;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Next.Runtime.Tests;

[TestClass]
public class CliTests {
  private static string LegacyGaTsp => Path.Combine(AppContext.BaseDirectory, "TestData", "GA_TSP.hl");

  private static async Task<(int Exit, string Out, string Err)> Hl(params string[] args) {
    var output = new StringWriter();
    var error = new StringWriter();
    int exit = await CliApp.RunAsync(args, output, error);
    return (exit, output.ToString(), error.ToString());
  }

  [TestMethod]
  public async Task RunWritesReportWithLegacyResults() {
    var reportPath = Path.Combine(Path.GetTempPath(), $"hl-cli-test-{Guid.NewGuid():N}.json");
    try {
      var (exit, output, _) = await Hl("run", LegacyGaTsp, "--seed", "0", "--quiet", "--out", reportPath);
      Assert.AreEqual(CliApp.ExitCompleted, exit, output);
      StringAssert.Contains(output, "CurrentBestQuality = 12332");

      using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
      Assert.AreEqual("Completed", report.RootElement.GetProperty("outcome").GetString());
      var results = report.RootElement.GetProperty("runs")[0].GetProperty("results");
      Assert.AreEqual(14538.0, results.GetProperty("CurrentWorstQuality").GetProperty("value").GetDouble());
    } finally {
      File.Delete(reportPath);
    }
  }

  [TestMethod]
  public async Task TimeoutStopsTheRun() {
    var (exit, output, _) = await Hl("run", LegacyGaTsp, "--seed", "0", "--quiet", "--timeout", "00:00:00.300");
    // GA_TSP needs about 2 s, so the timeout always hits
    Assert.AreEqual(CliApp.ExitStopped, exit, output);
    StringAssert.Contains(output, "Stopped");
  }

  [TestMethod]
  public async Task InfoDescribesLegacyFile() {
    var (exit, output, _) = await Hl("info", LegacyGaTsp, "--json");
    Assert.AreEqual(CliApp.ExitCompleted, exit);
    using var info = JsonDocument.Parse(output);
    Assert.AreEqual("HeuristicLab.Algorithms.GeneticAlgorithm.GeneticAlgorithm", info.RootElement.GetProperty("type").GetString());
    Assert.AreEqual("100", info.RootElement.GetProperty("parameters").GetProperty("PopulationSize").GetString());
  }

  [TestMethod]
  public async Task ListShowsAlgorithms() {
    var (exit, output, _) = await Hl("list", "algorithms");
    Assert.AreEqual(CliApp.ExitCompleted, exit);
    StringAssert.Contains(output, "HeuristicLab.Algorithms.GeneticAlgorithm.GeneticAlgorithm");
  }

  [TestMethod]
  public async Task NewRepeatStoreSummaryExportWorkflow() {
    var dir = Path.Combine(Path.GetTempPath(), $"hl-cli-workflow-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    var file = Path.Combine(dir, "ga.hl");
    var store = Path.Combine(dir, "store");
    try {
      var created = await Hl("new", "GeneticAlgorithm", "--problem", "TravelingSalesmanProblem", "--instance", "berlin52",
        "--set", "PopulationSize=20", "--set", "MaximumGenerations=10", "--out", file);
      Assert.AreEqual(CliApp.ExitCompleted, created.Exit, created.Out);
      StringAssert.Contains(created.Out, "berlin52");

      var run = await Hl("run", file, "--repeat", "3", "--parallel", "2", "--seed", "5", "--store", store, "--quiet");
      Assert.AreEqual(CliApp.ExitCompleted, run.Exit, run.Out + run.Err);
      StringAssert.Contains(run.Out, "3 of 3 runs completed, seeds 5..7");
      Assert.AreEqual(3, Directory.GetFiles(store, "*.json").Length);

      var summary = await Hl("store", "summary", store, "--by", "param:PopulationSize");
      Assert.AreEqual(CliApp.ExitCompleted, summary.Exit);
      StringAssert.Contains(summary.Out, "BestQuality by param:PopulationSize");

      var csv = Path.Combine(dir, "runs.csv");
      var export = await Hl("store", "export", store, "--csv", csv);
      Assert.AreEqual(CliApp.ExitCompleted, export.Exit);
      Assert.AreEqual(4, File.ReadAllLines(csv).Length);
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [TestMethod]
  public async Task NewRejectsAProblemTheAlgorithmCannotSolve() {
    var file = Path.Combine(Path.GetTempPath(), $"hl-cli-{Guid.NewGuid():N}.hl");
    var (exit, output, _) = await Hl("new", "GeneticAlgorithm", "--problem", "RegressionProblem", "--out", file);
    Assert.AreEqual(CliApp.ExitUsage, exit);
    StringAssert.Contains(output, "cannot solve");
    Assert.IsFalse(File.Exists(file));
  }

  [TestMethod]
  public async Task UnknownParameterIsAUsageError() {
    var (exit, _, error) = await Hl("run", LegacyGaTsp, "--set", "NoSuchParameter=1", "--quiet");
    Assert.AreEqual(CliApp.ExitUsage, exit);
    StringAssert.Contains(error, "no parameter 'NoSuchParameter'");
  }

  [TestMethod]
  public async Task BadInputIsAUsageError() {
    var (missing, _, _) = await Hl("run", "/nonexistent/file.hl");
    Assert.AreNotEqual(CliApp.ExitCompleted, missing);
    var (unknownKind, _, _) = await Hl("list", "operators");
    Assert.AreNotEqual(CliApp.ExitCompleted, unknownKind);
  }
}
