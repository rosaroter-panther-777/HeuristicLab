using System.Globalization;
using HeuristicLab.Algorithms.GeneticAlgorithm;
using HeuristicLab.Optimization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Next.Runtime.Tests;

[TestClass]
public class SolutionTests {
  private static string LegacyGaTsp => Path.Combine(AppContext.BaseDirectory, "TestData", "GA_TSP.hl");

  private static string LinearCsv() {
    var path = Path.Combine(Path.GetTempPath(), $"hl-sol-{Guid.NewGuid():N}.csv");
    File.WriteAllLines(path, new[] { "x1,x2,y" }.Concat(Enumerable.Range(0, 50).Select(i =>
      string.Create(CultureInfo.InvariantCulture, $"{i * 0.3},{Math.Cos(i)},{4 * i * 0.3 - 2 * Math.Cos(i) + 0.5}"))));
    return path;
  }

  [TestMethod]
  public async Task LinearRegressionSolutionShowsFormulaAndPredictions() {
    var csv = LinearCsv();
    try {
      var lr = Setups.Create(new SetupRequest("Linear Regression (LR)") { Problem = "Regression Problem", DataFile = csv, Target = "y", TrainingPercent = 80 }).Algorithm;
      await OptimizerRunner.RunAsync(lr);
      var view = Solutions.Find(lr).Single(v => v.Name == "Linear regression solution");

      StringAssert.Contains(view.Model, "x1");
      StringAssert.Contains(view.Model, "x2");
      Assert.AreEqual((0, 40), view.Training);
      Assert.AreEqual((40, 50), view.Test);
      Assert.AreEqual(50, view.Predicted!.Count);
      for (int i = 0; i < 50; i++) Assert.AreEqual(view.Actual![i], view.Predicted[i], 1e-8);
      Assert.IsTrue(view.Metrics.Any(m => m.Key.Contains("(test)")), "test metrics expected");

      var csvOut = new StringWriter();
      Solutions.WritePredictionsCsv(view, csvOut);
      var lines = csvOut.ToString().TrimEnd().Split('\n');
      Assert.AreEqual(51, lines.Length);
      StringAssert.StartsWith(lines[41], "40,test,");
    } finally { File.Delete(csv); }
  }

  [TestMethod]
  public async Task TradingEquityMatchesHeuristicLabProfit() {
    var random = new System.Random(11);
    var close = new List<double> { 100 };
    for (int t = 1; t < 300; t++) close.Add(close[^1] * Math.Exp((random.NextDouble() - 0.5) * 0.03));
    var table = Features.Derive(new TabularData(["Close"], [close]), ["diff(Close)", "lag(logreturn(Close),1..3)"], dropIncomplete: true);
    var path = Path.Combine(Path.GetTempPath(), $"hl-trade-{Guid.NewGuid():N}.parquet");
    DataFiles.Write(table, path);
    try {
      var ga = (GeneticAlgorithm)Setups.Create(new SetupRequest("GeneticAlgorithm") {
        Problem = "Symbolic Trading Problem (single-objective)", DataFile = path, Target = "diff(Close)",
        Inputs = ["lag(logreturn(Close),1)", "lag(logreturn(Close),2)", "lag(logreturn(Close),3)"],
        Settings = ["PopulationSize=50", "MaximumGenerations=5"]
      }).Algorithm;
      await OptimizerRunner.RunAsync(ga, new RunOptions { Seed = 2 });
      var view = Solutions.Find(ga).First(v => v.Kind == SolutionKind.Trading);

      Assert.IsFalse(string.IsNullOrWhiteSpace(view.Model));
      Assert.IsTrue(view.Predicted!.All(s => s is -1 or 0 or 1), "signals are positions");
      Assert.AreEqual(view.Metrics["Profit (training)"], view.TrainingEquity![^1], 1e-9);
      // no look-ahead: the model may only read past rows (lagged variables with lag < 0)
      var model = (HeuristicLab.Problems.DataAnalysis.Symbolic.ISymbolicDataAnalysisModel)
        ((HeuristicLab.Problems.DataAnalysis.Trading.ISolution)ga.Results["Best training solution"].Value).Model;
      var nodes = model.SymbolicExpressionTree.IterateNodesPrefix().ToList();
      Assert.IsFalse(nodes.Any(n => n.GetType() == typeof(HeuristicLab.Problems.DataAnalysis.Symbolic.VariableTreeNode)), "unlagged variable");
      Assert.IsTrue(nodes.OfType<HeuristicLab.Problems.DataAnalysis.Symbolic.LaggedVariableTreeNode>().All(n => n.Lag < 0), "lag >= 0 reads the present");
      Assert.AreEqual(view.Metrics["Profit (test)"], view.TestEquity![^1], 1e-9);
    } finally { File.Delete(path); }
  }

  [TestMethod]
  public async Task TspTourIsClosedAndMatchesBestQuality() {
    var ga = (GeneticAlgorithm)Documents.Load(LegacyGaTsp);
    ga.MaximumGenerations.Value = 50;
    var report = await OptimizerRunner.RunAsync(ga, new RunOptions { Seed = 0 });
    var route = Solutions.Find(ga).Single(v => v.Kind == SolutionKind.Route);

    Assert.AreEqual(131, route.Route!.Count, "130 cities plus the return to the start");
    Assert.AreEqual(route.Route[0], route.Route[^1]);
    Assert.AreEqual(130, route.Route.Take(130).Distinct().Count());
    Assert.AreEqual((double)report.Runs.Single().Results["BestQuality"].Value!, route.Metrics["Length"]);
  }
}
