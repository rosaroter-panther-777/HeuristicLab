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
  public async Task BadInputIsAUsageError() {
    var (missing, _, _) = await Hl("run", "/nonexistent/file.hl");
    Assert.AreNotEqual(CliApp.ExitCompleted, missing);
    var (unknownKind, _, _) = await Hl("list", "operators");
    Assert.AreNotEqual(CliApp.ExitCompleted, unknownKind);
  }
}
