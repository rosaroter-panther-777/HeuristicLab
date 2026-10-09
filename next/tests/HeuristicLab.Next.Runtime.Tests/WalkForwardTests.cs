using System.Globalization;
using HeuristicLab.Optimization;
using HeuristicLab.Problems.DataAnalysis.Symbolic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Next.Runtime.Tests;

[TestClass]
public class WalkForwardTests {
  [TestMethod]
  public void RollingAndExpandingWindows() {
    var rolling = WalkForward.Windows(100, new WalkForwardOptions(40, 20));
    CollectionAssert.AreEqual(new[] { ((0, 40), (40, 60)), ((20, 60), (60, 80)), ((40, 80), (80, 100)) }, rolling.ToArray());
    var expanding = WalkForward.Windows(100, new WalkForwardOptions(40, 20) { Expanding = true, Start = 5 });
    CollectionAssert.AreEqual(new[] { ((5, 45), (45, 65)), ((5, 65), (65, 85)) }, expanding.ToArray());
    Assert.ThrowsExactly<ArgumentException>(() => WalkForward.Windows(50, new WalkForwardOptions(40, 20)));
  }

  /// <summary>y = x until row 60, then y = -x: only a model trained on the right rows explains this.</summary>
  private static string RegimeChangeCsv() {
    var path = Path.Combine(Path.GetTempPath(), $"hl-wf-{Guid.NewGuid():N}.csv");
    File.WriteAllLines(path, new[] { "x,y" }.Concat(Enumerable.Range(0, 100).Select(i => {
      double x = (i % 10) + 1;
      return string.Create(CultureInfo.InvariantCulture, $"{x},{(i < 60 ? x : -x)}");
    })));
    return path;
  }

  [TestMethod]
  public async Task EachFoldTrainsOnItsOwnWindow() {
    var csv = RegimeChangeCsv();
    var store = Path.Combine(Path.GetTempPath(), $"hl-wf-store-{Guid.NewGuid():N}");
    try {
      var lr = Setups.Create(new SetupRequest("Linear Regression (LR)") { Problem = "Regression Problem", DataFile = csv, Target = "y" }).Algorithm;
      var results = new ResultStore(store);
      var report = await WalkForward.RunAsync(lr, new WalkForwardOptions(40, 20), parallelism: 2,
        onFold: f => { lock (results) results.Add(f.Report); });

      Assert.AreEqual(3, report.Folds.Count);
      double TestMse(int fold) => (double)report.Folds.Single(f => f.Index == fold).Report.Runs.Single()
        .Results["Linear regression solution.Mean squared error (test)"].Value!;
      Assert.AreEqual(0, TestMse(0), 1e-9, "fold 0 trains and tests before the regime change");
      Assert.IsTrue(TestMse(1) > 10, "fold 1 trains on y=x and is tested after the change");
      Assert.AreEqual(3, report.TestSummary["Linear regression solution.Mean squared error (test)"].Count);

      var rows = results.Rows();
      CollectionAssert.AreEquivalent(new[] { "0", "1", "2" }, rows.Select(r => r["label:fold"]).ToArray());
      Assert.AreEqual("40..59", rows.Single(r => r["label:fold"] == "0")["label:test"]);
    } finally {
      File.Delete(csv);
      if (Directory.Exists(store)) Directory.Delete(store, recursive: true);
    }
  }

  [TestMethod]
  public void SymbolicFitnessPartitionFollowsTheFold() {
    var csv = RegimeChangeCsv();
    try {
      var ga = Setups.Create(new SetupRequest("GeneticAlgorithm") {
        Problem = "Symbolic Regression Problem (single-objective)", DataFile = csv, Target = "y"
      }).Algorithm;
      WalkForward.SetPartitions(ga, (20, 60), (60, 80));
      var problem = (ISymbolicDataAnalysisProblem)ga.Problem;
      Assert.AreEqual((20, 60), (problem.FitnessCalculationPartition.Start, problem.FitnessCalculationPartition.End));
      var data = ((HeuristicLab.Problems.DataAnalysis.IDataAnalysisProblem)ga.Problem).ProblemData;
      Assert.AreEqual((20, 60), (data.TrainingPartition.Start, data.TrainingPartition.End));
      Assert.AreEqual((60, 80), (data.TestPartition.Start, data.TestPartition.End));
    } finally { File.Delete(csv); }
  }
}
