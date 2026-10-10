using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using HeuristicLab.Core;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Next.Runtime.Visuals;
using HeuristicLab.Optimization;
using HeuristicLab.Studio.Controls;
using HeuristicLab.Studio.ViewModels;
using HeuristicLab.Studio.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Studio.Tests;

/// <summary>
/// Each problem family's results drawn by the real views (screenshots studio-visual-*.png in the
/// test bin folder), and one live case: the ant trail while its experiment runs.
/// </summary>
[TestClass]
public class StudioVisualizationTests {
  private static HeadlessUnitTestSession session = null!;

  [ClassInitialize]
  public static void Start(TestContext _) => session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

  private static IAlgorithm Run(string problem, string algorithm = "GeneticAlgorithm", string? instance = null, params string[] settings) {
    var probe = (IProblem)Catalog.Find(Catalog.Problems(), problem)!.CreateInstance();
    instance ??= ProblemInstances.For(probe).FirstOrDefault()?.QualifiedName;
    var setup = Setups.Create(new SetupRequest(algorithm) { Problem = problem, Instance = instance, Settings = settings });
    var report = OptimizerRunner.RunAsync(setup.Algorithm, new RunOptions { Seed = 3, Timeout = TimeSpan.FromSeconds(30) }).GetAwaiter().GetResult();
    Assert.IsNull(report.Error, report.Error);
    return setup.Algorithm;
  }

  private sealed record Case(string File, string Problem, string Result, Type Control, string Algorithm = "GeneticAlgorithm",
                             string? Instance = null, string[]? Settings = null, string? Picture = null);

  [TestMethod]
  [Timeout(600_000)]
  public async Task EveryFamilyIsDrawnByItsView() {
    string[] fewGenerations = ["MaximumGenerations=20"];
    Case[] cases = [
      new("ant-trail", "HeuristicLab.Problems.GeneticProgramming.ArtificialAnt.Problem", "Best Solution", typeof(SceneView), Settings: fewGenerations),
      new("ant-tree", "HeuristicLab.Problems.GeneticProgramming.ArtificialAnt.Problem", "Best Solution", typeof(SceneView), Settings: fewGenerations, Picture: "Tree"),
      new("tree-length-histogram", "HeuristicLab.Problems.GeneticProgramming.Boolean.EvenParityProblem", "Symbolic expression tree lengths", typeof(LineChart), Settings: fewGenerations),
      new("lawn", "HeuristicLab.Problems.GeneticProgramming.LawnMower.Problem", "Best Solution", typeof(SceneView), Settings: fewGenerations),
      new("binpacking-3d", "HeuristicLab.Problems.BinPacking3D.PermutationProblem", "Best Packing Solution", typeof(BoxesView), Settings: fewGenerations),
      new("binpacking-2d", "HeuristicLab.Problems.BinPacking2D.PermutationProblem", "Best Packing Solution", typeof(SceneView), Settings: fewGenerations),
      new("vrp", "HeuristicLab.Problems.VehicleRouting.VehicleRoutingProblem", "Best VRP Solution", typeof(SceneView), Settings: fewGenerations),
      new("jssp", "HeuristicLab.Problems.Scheduling.JobShopSchedulingProblem", "Best Scheduling Solution", typeof(SceneView), Instance: "ORLIB JSSP/ft06", Settings: fewGenerations),
      new("testfunction", "HeuristicLab.Problems.TestFunctions.SingleObjectiveTestFunctionProblem", "Best Solution", typeof(SceneView), Settings: fewGenerations),
      new("pareto", "HeuristicLab.Problems.TestFunctions.MultiObjective.MultiObjectiveTestFunctionProblem", "Scatterplot", typeof(LineChart), "NSGA2", Instance: "ZDT/ZDT1 Function", Settings: ["MaximumGenerations=30"]),
      new("knapsack", "HeuristicLab.Problems.Knapsack.KnapsackProblem", "Best Knapsack Solution", typeof(SceneView), Settings: fewGenerations),
      new("lap", "HeuristicLab.Problems.LinearAssignment.LinearAssignmentProblem", "Best LAP Solution", typeof(SceneView), Settings: fewGenerations),
      new("qap", "HeuristicLab.Problems.QuadraticAssignment.QuadraticAssignmentProblem", "Best QAP Solution", typeof(SceneView), Instance: "QAPLIB/nug12", Settings: fewGenerations),
      new("orienteering", "HeuristicLab.Problems.Orienteering.OrienteeringProblem", "Best Orienteering Solution", typeof(SceneView), "VariableNeighborhoodSearch", Settings: ["MaximumIterations=20"]),
      new("ptsp", "HeuristicLab.Problems.PTSP.EstimatedProbabilisticTravelingSalesmanProblem", "Best PTSP Solution", typeof(SceneView), Settings: ["MaximumGenerations=3"]),
      new("regression-line", "HeuristicLab.Problems.DataAnalysis.Symbolic.Regression.SymbolicRegressionSingleObjectiveProblem", "Best training solution", typeof(LineChart), Settings: fewGenerations),
      new("regression-scatter", "HeuristicLab.Problems.DataAnalysis.Symbolic.Regression.SymbolicRegressionSingleObjectiveProblem", "Best training solution", typeof(LineChart), Settings: fewGenerations, Picture: "Scatter plot"),
      new("regression-residuals", "HeuristicLab.Problems.DataAnalysis.Symbolic.Regression.SymbolicRegressionSingleObjectiveProblem", "Best training solution", typeof(LineChart), Settings: fewGenerations, Picture: "Residuals"),
      new("regression-tree", "HeuristicLab.Problems.DataAnalysis.Symbolic.Regression.SymbolicRegressionSingleObjectiveProblem", "Best training solution", typeof(SceneView), Settings: fewGenerations, Picture: "Tree"),
      new("classification-confusion", "HeuristicLab.Problems.DataAnalysis.Symbolic.Classification.SymbolicClassificationSingleObjectiveProblem", "Best training solution", typeof(SceneView), Settings: fewGenerations, Picture: "Confusion matrix (training)"),
      new("trading-equity", "HeuristicLab.Problems.DataAnalysis.Trading.Symbolic.SingleObjectiveProblem", "Best training solution", typeof(LineChart), Settings: fewGenerations),
      new("clusters", "HeuristicLab.Problems.DataAnalysis.ClusteringProblem", "k-Means clustering solution", typeof(LineChart), "KMeansClustering"),
    ];
    // the algorithms run off the UI thread; the views draw their results
    var results = cases.AsParallel().WithDegreeOfParallelism(4)
      .Select(c => (Case: c, Item: Run(c.Problem, c.Algorithm, c.Instance, c.Settings ?? []).Results[c.Result].Value)).ToList();

    var problems = await session.Dispatch(() => {
      var failures = new List<string>();
      foreach (var (c, item) in results) {
        var detail = new ResultDetailViewModel(c.Result, item, new EditContext(_ => { }, () => false));
        if (c.Picture != null) detail.Visuals.SelectedIndex = detail.Visuals.Titles.ToList().IndexOf(c.Picture);
        var window = new Window { Content = new ResultDetailView { DataContext = detail }, Width = 1000, Height = 700 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        if (!detail.HasVisualization) failures.Add($"{c.File}: no visualization");
        else if (!window.GetVisualDescendants().Any(v => v.GetType() == c.Control && v.IsVisible))
          failures.Add($"{c.File}: {c.Control.Name} not shown (shows {detail.Visuals.Current?.GetType().Name})");
        window.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, $"studio-visual-{c.File}.png"));
        window.Close();
      }
      return failures;
    }, CancellationToken.None);
    Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
  }

  [TestMethod]
  [Timeout(300_000)]
  public async Task AntTrailFollowsTheRunningExperiment() {
    var file = await session.Dispatch(async () => {
      var vm = new MainViewModel(null, null);
      var ws = vm.Workspace;
      var window = new MainWindow { DataContext = vm, Width = 1440, Height = 880 };
      window.Show();
      var experiment = ws.NewExperiment();
      var block = ws.AddAlgorithm(experiment, Catalog.Find(Catalog.Algorithms(), "GeneticAlgorithm")!);
      ws.SetProblem(block, (IProblem)Catalog.Find(Catalog.Problems(), "HeuristicLab.Problems.GeneticProgramming.ArtificialAnt.Problem")!.CreateInstance());
      ParameterEditor.Apply(block.Algorithm, ["MaximumGenerations=100000"]);
      ws.Selected = block;
      var detail = (AlgorithmDetailViewModel)ws.Detail!;
      Assert.AreEqual("World", detail.ProblemVisuals.Title, "the problem tab shows the ant's world");
      detail.SelectedTab = AlgorithmDetailViewModel.ResultsTab;

      var run = ws.StartAllCommand.ExecuteAsync(null);
      async Task Until(Func<bool> condition, string what) {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition()) {
          if (clock.Elapsed > TimeSpan.FromSeconds(60)) Assert.Fail($"timed out waiting for {what}");
          await Task.Delay(100);
          ws.RefreshLive();
        }
      }
      await Until(() => detail.Results.Entries.Any(e => e.Name == "Best Solution"), "the best solution");
      detail.Results.Selected = detail.Results.Entries.Single(e => e.Name == "Best Solution");
      var trail = (SceneVisual)detail.Results.Detail!.Visuals.Current!;
      Assert.AreEqual("Ant trail", trail.Title);
      string Eaten() => ((SceneVisual)detail.Results.Detail!.Visuals.Current!).Notes[0];
      // the trail is redrawn as better ants are found; eventually it eats more than the first best ant
      var first = Eaten();
      await Until(() => Eaten() != first, "a better ant");
      Assert.AreEqual("Ant trail", detail.Results.Detail.Visuals.Title, "the chosen picture stays while the run goes on");
      Avalonia.Threading.Dispatcher.UIThread.RunJobs();
      var shot = Path.Combine(AppContext.BaseDirectory, "studio-visual-ant-live.png");
      window.CaptureRenderedFrame()!.Save(shot);
      ws.StopCommand.Execute(null);
      await run;
      window.Close();
      return shot;
    }, CancellationToken.None);
    Assert.IsTrue(File.Exists(file));
  }
}
