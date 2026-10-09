using HeuristicLab.Algorithms.GeneticAlgorithm;
using HeuristicLab.Cli;
using HeuristicLab.Problems.TravelingSalesman;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Next.Runtime.Tests;

[TestClass]
public class SetupTests {
  [TestMethod]
  public void SetupBuildsAlgorithmWithInstanceAndSettings() {
    var setup = Setups.Create(new SetupRequest("GeneticAlgorithm") {
      Problem = "TravelingSalesmanProblem", Instance = "berlin52", Settings = ["PopulationSize=30"], Name = "GA test"
    });
    var ga = (GeneticAlgorithm)setup.Algorithm;
    Assert.AreEqual("GA test", ga.Name);
    Assert.AreEqual(30, ga.PopulationSize.Value);
    Assert.AreEqual(52, ((TravelingSalesmanProblem)ga.Problem).Coordinates.Rows);
    StringAssert.Contains(setup.Messages.Single(), "berlin52");
  }

  [TestMethod]
  public void SetupRejectsInconsistentRequests() {
    StringAssert.Contains(Assert.ThrowsExactly<ArgumentException>(() =>
      Setups.Create(new SetupRequest("GeneticAlgorithm") { Instance = "berlin52" })).Message, "needs a problem");
    StringAssert.Contains(Assert.ThrowsExactly<ArgumentException>(() =>
      Setups.Create(new SetupRequest("NoSuchAlgorithm"))).Message, "Unknown algorithm");
  }

  [TestMethod]
  public void ProblemsForAlgorithmAreCompatible() {
    var ga = Catalog.Find(Catalog.Algorithms(), "GeneticAlgorithm")!;
    var problems = Setups.ProblemsFor(ga);
    Assert.IsTrue(problems.Any(p => p.Type == typeof(TravelingSalesmanProblem)));
    Assert.IsFalse(problems.Any(p => p.Name == "Regression Problem"), "GA cannot solve a plain regression problem");
  }

  [TestMethod]
  public void ParametersAreDescribedForEditing() {
    var parameters = ParameterEditor.Describe(new GeneticAlgorithm());
    var population = parameters.Single(p => p.Name == "PopulationSize");
    Assert.IsTrue(population.Editable);
    Assert.AreEqual(0, population.Choices.Count);
    var selector = parameters.Single(p => p.Name == "Selector");
    Assert.IsTrue(selector.Choices.Contains("TournamentSelector"));
    Assert.IsTrue(selector.Choices.Contains(selector.Value));
  }

  [TestMethod]
  public void TopLevelCommaSplitKeepsExpressions() {
    CollectionAssert.AreEqual(new[] { "a", "lag(logreturn(Close),1..3)", "std(x,20)" },
      Features.SplitList("a, lag(logreturn(Close),1..3) ,std(x,20)").ToArray());
  }

  [TestMethod]
  public async Task CliDataDeriveAndTradingSetup() {
    var dir = Path.Combine(Path.GetTempPath(), $"hl-cli-data-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var random = new System.Random(5);
      var prices = new List<string> { "Close,Volume" };
      double p = 100;
      for (int i = 0; i < 300; i++) { p *= Math.Exp((random.NextDouble() - 0.5) * 0.03); prices.Add(FormattableString.Invariant($"{p},{1000 + i}")); }
      File.WriteAllLines(Path.Combine(dir, "prices.csv"), prices);

      async Task<(int, string)> Hl(params string[] args) {
        var o = new StringWriter(); var e = new StringWriter();
        int exit = await CliApp.RunAsync(args, o, e);
        return (exit, o + e.ToString());
      }
      var features = Path.Combine(dir, "features.parquet");
      var (deriveExit, deriveOut) = await Hl("data", "derive", Path.Combine(dir, "prices.csv"),
        "--add", "diff(Close)", "--add", "lag(logreturn(Close),1..2)", "--dropna", "--out", features);
      Assert.AreEqual(CliApp.ExitCompleted, deriveExit, deriveOut);
      StringAssert.Contains(deriveOut, "297 rows, 5 columns (3 new)");

      var (infoExit, infoOut) = await Hl("data", "info", features);
      Assert.AreEqual(CliApp.ExitCompleted, infoExit);
      StringAssert.Contains(infoOut, "lag(logreturn(Close),2)");

      var setup = Path.Combine(dir, "trading.hl");
      var (newExit, newOut) = await Hl("new", "GeneticAlgorithm", "--problem", "Symbolic Trading Problem (single-objective)",
        "--data", features, "--target", "diff(Close)", "--inputs", "lag(logreturn(Close),1..2)",
        "--set", "PopulationSize=30", "--set", "MaximumGenerations=3", "--out", setup);
      Assert.AreEqual(CliApp.ExitCompleted, newExit, newOut);
      StringAssert.Contains(newOut, "trading data: 297 rows, target diff(Close), 2 inputs");

      var (runExit, runOut) = await Hl("run", setup, "--seed", "1", "--quiet");
      Assert.AreEqual(CliApp.ExitCompleted, runExit, runOut);
      StringAssert.Contains(runOut, "Sharpe ratio (test)");
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
