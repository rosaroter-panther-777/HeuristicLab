using System.Collections;
using HeuristicLab.Algorithms.DataAnalysis;
using HeuristicLab.Algorithms.GeneticAlgorithm;
using HeuristicLab.Optimization;
using HeuristicLab.Problems.DataAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TradingProblem = HeuristicLab.Problems.DataAnalysis.Trading.Symbolic.SingleObjectiveProblem;

namespace HeuristicLab.Next.Runtime.Tests;

[TestClass]
public class DataTests {
  private static readonly double[] Prices = [100, 110, 99, 99, 108.9];

  private static TabularData PriceTable() => new(
    ["Date", "Close", "Ticker"],
    [Enumerable.Range(0, 5).Select(i => new DateTime(2026, 1, 1).AddDays(i)).ToList(),
     Prices.ToList(), Enumerable.Repeat("ABC", 5).ToList()]);

  private static string Temp(string extension) => Path.Combine(Path.GetTempPath(), $"hl-data-{Guid.NewGuid():N}{extension}");

  private static void AssertSeries(double[] expected, IList actual, double tolerance = 1e-12) {
    var a = ((List<double>)actual).ToArray();
    Assert.AreEqual(expected.Length, a.Length);
    for (int i = 0; i < a.Length; i++)
      if (double.IsNaN(expected[i])) Assert.IsTrue(double.IsNaN(a[i]), $"row {i}: expected NaN, got {a[i]}");
      else Assert.AreEqual(expected[i], a[i], tolerance, $"row {i}");
  }

  [TestMethod]
  public void ParquetRoundTripKeepsTypesAndValues() {
    var path = Temp(".parquet");
    try {
      DataFiles.Write(PriceTable(), path);
      var read = DataFiles.Read(path);
      CollectionAssert.AreEqual(new[] { "Date", "Close", "Ticker" }, read.Names.ToArray());
      CollectionAssert.AreEqual(Prices, read.Numbers("Close").ToArray());
      Assert.AreEqual(new DateTime(2026, 1, 3), ((List<DateTime>)read.Column("Date"))[2]);
      Assert.AreEqual("ABC", ((List<string>)read.Column("Ticker"))[4]);
    } finally { File.Delete(path); }
  }

  [TestMethod]
  public void CsvRoundTripKeepsNumbers() {
    var path = Temp(".csv");
    try {
      DataFiles.Write(PriceTable(), path);
      CollectionAssert.AreEqual(Prices, DataFiles.Read(path).Numbers("Close").ToArray());
    } finally { File.Delete(path); }
  }

  [TestMethod]
  public void FeatureFormulas() {
    var t = Features.Derive(PriceTable(), [
      "return(Close)", "logreturn(Close)", "diff(Close)", "lag(Close,1..2)", "lead(Close,1)",
      "mean(Close,2)", "std(Close,3)", "zscore(Close,3)", "zscore(Close,2)", "max(Close,3)", "std(logreturn(Close),2)"]);
    double n = double.NaN;
    AssertSeries([n, 0.1, -0.1, 0, 0.1], t.Column("return(Close)"));
    AssertSeries([n, Math.Log(1.1), Math.Log(0.9), 0, Math.Log(1.1)], t.Column("logreturn(Close)"));
    AssertSeries([n, 10, -11, 0, 9.9], t.Column("diff(Close)"), 1e-9);
    AssertSeries([n, 100, 110, 99, 99], t.Column("lag(Close,1)"));
    AssertSeries([n, n, 100, 110, 99], t.Column("lag(Close,2)"));
    AssertSeries([110, 99, 99, 108.9, n], t.Column("lead(Close,1)"));
    AssertSeries([n, 105, 104.5, 99, 103.95], t.Column("mean(Close,2)"));
    // sample std over windows (100,110,99), (110,99,99), (99,99,108.9)
    static double Sd(params double[] w) { var m = w.Average(); return Math.Sqrt(w.Sum(v => (v - m) * (v - m)) / (w.Length - 1)); }
    AssertSeries([n, n, Sd(100, 110, 99), Sd(110, 99, 99), Sd(99, 99, 108.9)], t.Column("std(Close,3)"), 1e-9);
    Assert.AreEqual(Math.Sqrt(37), Sd(100, 110, 99), 1e-12);
    Assert.AreEqual((99 - 103) / Math.Sqrt(37), t.Numbers("zscore(Close,3)")[2], 1e-12);
    Assert.IsTrue(double.IsNaN(t.Numbers("zscore(Close,2)")[3]), "zero volatility (99, 99) gives NaN, not infinity");
    AssertSeries([n, n, 110, 110, 108.9], t.Column("max(Close,3)"));
    Assert.AreEqual(Math.Abs(Math.Log(1.1) - Math.Log(0.9)) / Math.Sqrt(2), t.Numbers("std(logreturn(Close),2)")[2], 1e-12);
  }

  [TestMethod]
  public void DropIncompleteRemovesWarmUpAndLookAheadRows() {
    var t = Features.Derive(PriceTable(), ["lag(Close,2)", "lead(Close,1)"], dropIncomplete: true);
    Assert.AreEqual(2, t.Rows);
    CollectionAssert.AreEqual(new double[] { 99, 99 }, t.Numbers("Close").ToArray());
  }

  [TestMethod]
  public void UnknownColumnsAndFunctionsAreReported() {
    StringAssert.Contains(Assert.ThrowsExactly<ArgumentException>(() => Features.Derive(PriceTable(), ["return(Open)"])).Message, "Close");
    StringAssert.Contains(Assert.ThrowsExactly<ArgumentException>(() => Features.Derive(PriceTable(), ["median(Close,3)"])).Message, "median");
    StringAssert.Contains(Assert.ThrowsExactly<ArgumentException>(() => Features.Derive(PriceTable(), ["lag(Close)"])).Message, "whole number");
  }

  [TestMethod]
  public void RegressionProblemGetsRegressionDataNotTimeSeries() {
    var table = RandomWalk(200, seed: 1);
    var regression = new RegressionProblem();
    StringAssert.StartsWith(DataProblems.Load(regression, table, "Close"), "regression");
    Assert.IsNotInstanceOfType<ITimeSeriesPrognosisProblemData>(regression.ProblemData);
    Assert.AreEqual(132, regression.ProblemData.TrainingPartition.End);
    Assert.AreEqual(200, regression.ProblemData.TestPartition.End);

    var timeSeries = new TimeSeriesPrognosisProblem();
    StringAssert.StartsWith(DataProblems.Load(timeSeries, table, "Close"), "time series");
  }

  [TestMethod]
  public async Task AutoregressiveModelRecoversAnAr1Process() {
    // y[t] = 0.8 y[t-1] + e, e ~ U(-0.1, 0.1): an AR(1) model's training RMSE is about sd(e) = 0.058
    var random = new System.Random(7);
    var y = new List<double> { 0 };
    for (int t = 1; t < 600; t++) y.Add(0.8 * y[^1] + (random.NextDouble() - 0.5) * 0.2);
    var path = Temp(".parquet");
    try {
      DataFiles.Write(new TabularData(["y"], [y]), path);
      var ar = new AutoregressiveModeling { TimeOffset = 1 };
      var problem = new TimeSeriesPrognosisProblem();
      DataProblems.Load(problem, path, "y", trainingPercent: 80);
      // AR reads y[row - TimeOffset]: training must not start before row TimeOffset
      problem.ProblemData.TrainingPartition.Start = ar.TimeOffset;
      ar.Problem = problem;
      var report = await OptimizerRunner.RunAsync(ar);
      Assert.AreEqual(RunOutcome.Completed, report.Outcome, report.Error);
      Assert.AreEqual(0.2 / Math.Sqrt(12), (double)report.Runs.Single().Results["Root mean square error"].Value!, 0.006);
    } finally { File.Delete(path); }
  }

  [TestMethod]
  public async Task SymbolicTradingStrategyTrainsOnDerivedFeatures() {
    var table = Features.Derive(RandomWalk(400, seed: 3),
      ["diff(Close)", "lag(logreturn(Close),1..3)", "std(logreturn(Close),10)"], dropIncomplete: true);
    var trading = new TradingProblem();
    StringAssert.StartsWith(DataProblems.Load(trading, table, "diff(Close)"), "trading");
    var ga = new GeneticAlgorithm { Problem = trading };
    ParameterEditor.Apply(ga, ["PopulationSize=50", "MaximumGenerations=5"]);
    var report = await OptimizerRunner.RunAsync(ga, new RunOptions { Seed = 1 });
    Assert.AreEqual(RunOutcome.Completed, report.Outcome, report.Error);
    Assert.AreEqual(5, report.Runs.Single().Results["Generations"].Value);
  }

  private static TabularData RandomWalk(int rows, int seed) {
    var random = new System.Random(seed);
    var close = new List<double> { 100 };
    for (int t = 1; t < rows; t++) close.Add(close[^1] * Math.Exp((random.NextDouble() - 0.5) * 0.04));
    var volume = Enumerable.Range(0, rows).Select(_ => 1000.0 + random.Next(500)).ToList();
    return new TabularData(["Close", "Volume"], [close, volume]);
  }
}
