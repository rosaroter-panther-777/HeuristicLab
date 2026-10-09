using HeuristicLab.Optimization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Next.Runtime.Tests;

[TestClass]
public class ExperimentTreeTests {
  private static IAlgorithm TabuSearchOnTsp(string? instance = "ch130") =>
    Setups.Create(new SetupRequest("TabuSearch") { Problem = instance == null ? null : "TravelingSalesmanProblem", Instance = instance }).Algorithm;

  private static Type TabuSearchType => Catalog.Find(Catalog.Algorithms(), "TabuSearch")!.Type;

  [TestMethod]
  public void ContainerTypesAreDiscovered() {
    var names = ExperimentTree.ContainerTypes().Select(e => e.Type).ToList();
    CollectionAssert.IsSubsetOf(new[] { typeof(Experiment), typeof(BatchRun), typeof(TimeLimitRun) }, names);
  }

  [TestMethod]
  public void BatchWithSeveralChildrenUsesAnInnerExperiment() {
    var batch = new BatchRun();
    var first = TabuSearchOnTsp();
    ExperimentTree.Add(batch, first);
    ExperimentTree.Add(batch, TabuSearchOnTsp());
    Assert.AreEqual(2, ExperimentTree.Children(batch).Count);
    Assert.IsInstanceOfType<Experiment>(batch.Optimizer);

    // a legacy batch run around a single algorithm: adding wraps it
    var legacy = new BatchRun { Optimizer = TabuSearchOnTsp() };
    ExperimentTree.Add(legacy, TabuSearchOnTsp());
    Assert.AreEqual(2, ExperimentTree.Children(legacy).Count);

    ExperimentTree.Remove(batch, first);
    Assert.AreEqual(1, ExperimentTree.Children(batch).Count);
  }

  [TestMethod]
  public void TimeLimitRunTakesOneAlgorithm() {
    var run = new TimeLimitRun();
    Assert.IsTrue(ExperimentTree.CanAdd(run, TabuSearchType));
    Assert.IsFalse(ExperimentTree.CanAdd(run, typeof(BatchRun)));
    ExperimentTree.Add(run, TabuSearchOnTsp());
    Assert.IsFalse(ExperimentTree.CanAdd(run, TabuSearchType));
  }

  [TestMethod]
  public void ProblemsListWhatIsMissing() {
    var experiment = new Experiment();
    CollectionAssert.AreEqual(new[] { "Experiment is empty." }, ExperimentTree.Problems(experiment).ToArray());
    var batch = new BatchRun();
    ExperimentTree.Add(experiment, TabuSearchOnTsp());
    ExperimentTree.Add(experiment, batch);
    ExperimentTree.Add(batch, TabuSearchOnTsp(instance: null));
    var problems = ExperimentTree.Problems(experiment);
    Assert.AreEqual(1, problems.Count, string.Join("; ", problems));
    StringAssert.Contains(problems[0], "has no problem");
    Assert.AreEqual(2, ExperimentTree.Algorithms(experiment).Count());
  }

  [TestMethod]
  public async Task BuiltExperimentRunsEveryBatchChildRepeatedlyAndSurvivesSaving() {
    var experiment = new Experiment { Name = "Built" };
    var batch = new BatchRun { Repetitions = 3 };
    ExperimentTree.Add(experiment, batch);
    foreach (var _ in Enumerable.Range(0, 2)) {
      var ts = TabuSearchOnTsp("berlin52");
      ParameterEditor.Apply(ts, ["MaximumIterations=20"]);
      ExperimentTree.Add(batch, ts);
    }
    var report = await OptimizerRunner.RunAsync(experiment);
    Assert.AreEqual(RunOutcome.Completed, report.Outcome, report.Error);
    Assert.AreEqual(6, experiment.Runs.Count, "3 repetitions x 2 algorithms");

    var path = Path.Combine(Path.GetTempPath(), $"hl-tree-{Guid.NewGuid():N}.hl");
    try {
      Documents.Save(experiment, path);
      var loaded = (Experiment)Documents.Load(path)!;
      var loadedBatch = (BatchRun)ExperimentTree.Children(loaded).Single();
      Assert.AreEqual(2, ExperimentTree.Children(loadedBatch).Count);
      Assert.AreEqual(6, loaded.Runs.Count);
    } finally { File.Delete(path); }
  }
}
