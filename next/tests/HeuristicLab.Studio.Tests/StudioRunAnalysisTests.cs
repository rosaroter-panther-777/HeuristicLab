using Avalonia.Headless;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Next.Runtime.Visuals;
using HeuristicLab.Optimization;
using HeuristicLab.Studio.ViewModels;
using HeuristicLab.Studio.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Studio.Tests;

/// <summary>The Results tab: runs of the workspace's experiments as a table, grouped, charted and tested.</summary>
[TestClass]
public class StudioRunAnalysisTests {
  private static HeadlessUnitTestSession session = null!;

  [ClassInitialize]
  public static void Start(TestContext _) => session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

  private static IProblem Ch130() =>
    (IProblem)Setups.Create(new SetupRequest("GeneticAlgorithm") { Problem = "TravelingSalesmanProblem", Instance = "ch130" }).Algorithm.Problem!.Clone();

  [TestMethod]
  [Timeout(300_000)]
  public async Task CompareBatchRunsOfAnExperiment() {
    await session.Dispatch(async () => {
      var vm = new MainViewModel(null, null);
      var ws = vm.Workspace;
      var window = new MainWindow { DataContext = vm, Width = 1500, Height = 960 };
      window.Show();
      void Shot(string name) {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, $"studio-results-{name}.png"));
      }

      // an experiment: a GA with population 20 and one with 60, five runs each, and one tabu search run
      var experiment = ws.NewExperiment();
      var batchRun = ExperimentTree.ContainerTypes().Single(e => e.Type == typeof(BatchRun));
      foreach (var size in new[] { 20, 60 }) {
        var batch = ws.AddContainer(experiment, batchRun);
        batch.RepetitionsText = "5";
        var ga = ws.AddAlgorithm(batch, Catalog.Find(Catalog.Algorithms(), "GeneticAlgorithm")!);
        ws.SetProblem(ga, Ch130());
        ParameterEditor.Apply(ga.Algorithm, [$"PopulationSize={size}", "MaximumGenerations=25"]);
      }
      var ts = ws.AddAlgorithm(experiment, Catalog.Find(Catalog.Algorithms(), "TabuSearch")!);
      ws.SetProblem(ts, Ch130());
      ParameterEditor.Apply(ts.Algorithm, ["MaximumIterations=10"]);
      await ws.StartAllCommand.ExecuteAsync(null);

      vm.SelectedTab = MainViewModel.ResultsTab;
      var analysis = vm.RunAnalysis;
      var root = analysis.Sources.Single();
      Assert.AreEqual(3, root.Children.Count, "two batch runs and the tabu search");
      root.IsChecked = true;
      Assert.AreEqual(11, analysis.Table.Rows.Count, analysis.Status);

      // group by where the runs were made: one group per batch run and the tabu search
      analysis.GroupToAdd = analysis.AllColumns.Single(c => c.Key == "origin");
      Assert.AreEqual(3, analysis.Groups.Count);
      Assert.IsTrue(analysis.TableColumns.Any(c => c.Key == "param:PopulationSize"), "a parameter that differs between runs is shown");
      analysis.SortCommand.Execute("result:BestQuality");
      var best = analysis.Rows.Select(r => r.Row.Number("result:BestQuality")!.Value).ToList();
      CollectionAssert.AreEqual(best.Order().ToList(), best, "sorted by quality");
      Shot("table");

      // box plot of the best quality per group
      analysis.SelectedView = RunAnalysisViewModel.ChartsView;
      Assert.AreEqual("Box plot", analysis.ChartKind);
      Assert.AreEqual("result:BestQuality", analysis.ChartValue!.Key);
      var box = (SceneVisual)analysis.Chart.Current!;
      Assert.AreEqual(3, box.Legend.Count);
      Shot("boxplot");
      analysis.ChartKind = "Curves";
      Assert.AreEqual("Qualities", analysis.CurveTable);
      Assert.AreEqual("BestQuality", analysis.CurveRow);
      Assert.AreEqual(6, ((ChartPanel)analysis.Chart.Current!).Series.Count, "runs and mean of three groups");
      Shot("curves");

      // only the two GAs, grouped by population size: statistics and tests
      analysis.AddFilterCommand.Execute(null);
      var filter = analysis.Filters.Single();
      filter.Column = analysis.AllColumns.Single(c => c.Key == "param:Algorithm Type");
      filter.Op = "contains";
      filter.Value = "Genetic";
      Assert.AreEqual(10, analysis.Table.Rows.Count);
      analysis.RemoveGroupCommand.Execute(analysis.GroupBy.Single());
      analysis.GroupToAdd = analysis.AllColumns.Single(c => c.Key == "param:PopulationSize");
      CollectionAssert.AreEqual(new[] { "PopulationSize 20", "PopulationSize 60" }, analysis.Groups.Select(g => g.Name).ToList());
      analysis.SelectedView = RunAnalysisViewModel.StatisticsView;
      Assert.AreEqual(2, analysis.Summaries.Count);
      Assert.IsTrue(analysis.Summaries.All(s => s.Count == "5"));
      Assert.AreEqual(1, analysis.Pairs.Count);
      StringAssert.StartsWith(analysis.AllGroupsTest, "Kruskal-Wallis over all 2 groups");
      Shot("statistics");

      // one run: its parameters and results, with the problem's picture of its best solution
      analysis.SelectedView = RunAnalysisViewModel.RunView;
      analysis.SelectedRow = analysis.Rows[0];
      Assert.AreEqual("Best TSP Solution", analysis.SelectedRunResult!.Name);
      Assert.IsTrue(analysis.RunResultDetail!.HasVisualization);
      Shot("run");

      var csv = Path.Combine(Path.GetTempPath(), $"runs-{Guid.NewGuid():N}.csv");
      analysis.Export(csv);
      var lines = File.ReadAllLines(csv);
      Assert.AreEqual(11, lines.Length, "header and ten runs");
      StringAssert.Contains(lines[0], "BestQuality (result)");
      File.Delete(csv);
      window.Close();
      return 0;
    }, CancellationToken.None);
  }
}
