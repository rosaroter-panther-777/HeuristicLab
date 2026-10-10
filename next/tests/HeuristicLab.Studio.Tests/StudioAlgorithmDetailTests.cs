using Avalonia.Headless;
using HeuristicLab.Core;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Next.Runtime.Visuals;
using HeuristicLab.Optimization;
using HeuristicLab.Studio.ViewModels;
using HeuristicLab.Studio.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Studio.Tests;

[TestClass]
public class StudioAlgorithmDetailTests {
  private static HeadlessUnitTestSession session = null!;

  [ClassInitialize]
  public static void Start(TestContext _) => session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

  private static IProblem Ch130() =>
    (IProblem)Setups.Create(new SetupRequest("TabuSearch") { Problem = "TravelingSalesmanProblem", Instance = "ch130" }).Algorithm.Problem!.Clone();

  [TestMethod]
  public async Task ResultsUpdateLiveWhileTheExperimentRuns() {
    var file = await session.Dispatch(async () => {
      var vm = new MainViewModel(null, null);
      var ws = vm.Workspace;
      var window = new MainWindow { DataContext = vm, Width = 1440, Height = 880 };
      window.Show();
      var experiment = ws.NewExperiment();
      var block = ws.AddAlgorithm(experiment, Catalog.Find(Catalog.Algorithms(), "TabuSearch")!);
      ws.SetProblem(block, Ch130());
      ParameterEditor.Apply(block.Algorithm, ["MaximumIterations=100000"]);
      ws.Selected = block;
      var detail = (AlgorithmDetailViewModel)ws.Detail!;
      detail.SelectedTab = AlgorithmDetailViewModel.ResultsTab;

      var run = ws.StartAllCommand.ExecuteAsync(null);
      Assert.IsTrue(ws.IsLive, "live monitoring runs while the experiment runs");
      async Task Until(Func<bool> condition, string what) {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition()) {
          if (clock.Elapsed > TimeSpan.FromSeconds(60)) Assert.Fail($"timed out waiting for {what}");
          await Task.Delay(100);
          ws.RefreshLive();
        }
      }
      string Value(string name) => detail.Results.Entries.Single(e => e.Name == name).Summary;

      // results appear while running, without any action
      await Until(() => detail.Results.Entries.Any(e => e.Name == "Best TSP Solution") && Value("Iterations") != "0", "results");
      var best = detail.Results.Entries.Single(e => e.Name == "Best TSP Solution");
      detail.Results.Selected = best;
      var tour = (SceneVisual)detail.Results.Detail!.Visuals.Current!;
      Assert.AreEqual(130, tour.Shapes.OfType<PathShape>().Single(p => p.Closed).Points.Count, "closed tour through 130 cities");

      // counters move on; the selection and its detail follow the live data
      var iterations = Value("Iterations");
      await Until(() => Value("Iterations") != iterations, "the next iterations");
      Assert.AreSame(best, detail.Results.Selected, "selection stays while results update");
      Assert.IsNotNull(detail.Results.Detail.Quality);
      Assert.AreEqual("6110", Value("BestKnownQuality"), "reference value stays");
      Assert.AreEqual("Started", detail.State);
      Assert.IsFalse(detail.PauseCommand.CanExecute(null), "the experiment controls its algorithms");
      Avalonia.Threading.Dispatcher.UIThread.RunJobs();
      var shot = Path.Combine(AppContext.BaseDirectory, "studio-detail-results-live.png");
      window.CaptureRenderedFrame()!.Save(shot);

      ws.StopCommand.Execute(null);
      await run;
      ws.RefreshLive();
      Assert.IsFalse(ws.IsLive);
      // visualization and quality belong to the same solution: the best quality found
      Assert.AreEqual(Value("BestQuality"), detail.Results.Detail.Quality);
      Assert.AreSame(best, detail.Results.Selected);
      window.Close();
      return shot;
    }, CancellationToken.None);
    Assert.IsTrue(new FileInfo(file).Length > 5_000);
  }

  [TestMethod]
  public async Task RunsAppearWhileTheBatchContinues() {
    await session.Dispatch(async () => {
      var ws = new ExperimentWorkspaceViewModel();
      var experiment = ws.NewExperiment();
      var batch = ws.AddContainer(experiment, ExperimentTree.ContainerTypes().Single(e => e.Type == typeof(BatchRun)));
      batch.RepetitionsText = "3";
      var block = ws.AddAlgorithm(batch, Catalog.Find(Catalog.Algorithms(), "TabuSearch")!);
      ws.SetProblem(block, Ch130());
      ParameterEditor.Apply(block.Algorithm, ["MaximumIterations=150"]);
      ws.Selected = batch;

      var run = ws.StartAllCommand.ExecuteAsync(null);
      var clock = System.Diagnostics.Stopwatch.StartNew();
      int seenWhileRunning = 0;
      while (!run.IsCompleted && clock.Elapsed < TimeSpan.FromMinutes(2)) {
        await Task.Delay(100);
        ws.RefreshLive();
        if (ws.IsRunningExperiments) seenWhileRunning = Math.Max(seenWhileRunning, batch.Runs.Rows.Count);
        if (seenWhileRunning > 0 && seenWhileRunning < 3) break;
      }
      Assert.IsTrue(seenWhileRunning is > 0 and < 3, $"a finished repetition shows while the batch continues (saw {seenWhileRunning})");
      // the algorithm's own Runs tab follows too
      ws.Selected = block;
      ws.RefreshLive();
      Assert.IsTrue(block.Detail.Runs.Rows.Count > 0);
      await run;
      ws.Selected = batch;
      ws.RefreshLive();
      Assert.AreEqual(3, batch.Runs.Rows.Count);
    }, CancellationToken.None);
  }

  [TestMethod]
  public async Task TabuSearchOnCh130ExposesWhatHeuristicLabShowed() {
    var shots = await session.Dispatch(async () => {
      var vm = new MainViewModel(null, null);
      var ws = vm.Workspace;
      var window = new MainWindow { DataContext = vm, Width = 1440, Height = 880 };
      window.Show();
      var files = new List<string>();
      void Shot(string name) {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var file = Path.Combine(AppContext.BaseDirectory, $"studio-detail-{name}.png");
        window.CaptureRenderedFrame()!.Save(file);
        files.Add(file);
      }

      var experiment = ws.NewExperiment();
      var block = ws.AddAlgorithm(experiment, Catalog.Find(Catalog.Algorithms(), "TabuSearch")!);
      ws.SetProblem(block, Ch130());
      block.IsExpanded = true;
      ws.Selected = block.Problem;
      var detail = (AlgorithmDetailViewModel)ws.Detail!;
      Assert.AreSame(block.Detail, detail);
      Assert.AreEqual(AlgorithmDetailViewModel.ProblemTab, detail.SelectedTab);

      // problem: source, parameters, structured values, visualization
      StringAssert.Contains(detail.ProblemSource, "TSPLIB");
      StringAssert.Contains(detail.ProblemSource, "ch130");
      var problemParameters = detail.ProblemParameters!.Parameters.Select(p => p.Name).ToList();
      CollectionAssert.IsSubsetOf(new[] { "BestKnownQuality", "BestKnownSolution", "Coordinates", "DistanceMatrix", "Evaluator", "SolutionCreator" }, problemParameters);
      var bestSolution = detail.ProblemParameters.Parameters.Single(p => p.Name == "BestKnownSolution");
      detail.ProblemParameters.Selected = bestSolution;
      Assert.IsTrue(bestSolution.Editor.IsTable);
      Assert.AreEqual(130, bestSolution.Editor.Rows.Count);
      Assert.AreEqual("Type: RelativeUndirected", bestSolution.Editor.Details);
      Assert.IsTrue(bestSolution.HasShowInRun);
      Assert.AreEqual("Best known solution (quality 6110)", detail.ProblemVisuals.Title);
      var known = (SceneVisual)detail.ProblemVisuals.Current!;
      Assert.AreEqual(130, known.Shapes.OfType<PathShape>().Single().Points.Count, "best known tour");
      Assert.AreEqual(130, known.Shapes.OfType<MarkerShape>().Count(), "locations");
      Shot("problem");

      // algorithm: typed parameters, choices with nested settings, analyzers
      detail.SelectedTab = AlgorithmDetailViewModel.AlgorithmTab;
      var names = detail.AlgorithmParameters.Parameters.Select(p => p.Name).ToList();
      CollectionAssert.IsSubsetOf(new[] { "Analyzer", "MaximumIterations", "MoveEvaluator", "MoveGenerator", "MoveMaker", "SampleSize",
                                          "Seed", "SetSeedRandomly", "TabuChecker", "TabuMaker", "TabuTenure" }, names);
      var generator = detail.AlgorithmParameters.Parameters.Single(p => p.Name == "MoveGenerator");
      Assert.IsTrue(generator.IsChoice && generator.Choices.Count > 1, $"{generator.Choices.Count}: {string.Join(", ", generator.Choices.Select(c => c.Display))}; value {generator.Summary}");
      StringAssert.StartsWith(generator.DataType, "IMoveGenerator (");
      Assert.IsTrue(detail.AlgorithmParameters.Parameters.Single(p => p.Name == "SetSeedRandomly").Editor.IsBool);
      var analyzer = detail.AlgorithmParameters.Parameters.Single(p => p.Name == "Analyzer");
      detail.AlgorithmParameters.Selected = analyzer;
      Assert.IsTrue(analyzer.Editor.IsCheckedList);
      Assert.IsTrue(analyzer.Editor.Entries.Count >= 3);
      var entry = analyzer.Editor.Entries[0];
      bool was = entry.IsChecked;
      entry.IsChecked = !was;
      Assert.AreEqual(!was, CheckedList.TryCreate(ItemInspector.Value(block.Algorithm.Parameters["Analyzer"]))!.IsChecked(0));
      entry.IsChecked = was;
      analyzer.ShowInRun = false;
      Assert.IsFalse(((IValueParameter)block.Algorithm.Parameters["Analyzer"]).GetsCollected);
      analyzer.ShowInRun = true;
      Shot("algorithm");

      // operator graph: workflow from the initial operator, operator details, nested main loop
      detail.SelectedTab = AlgorithmDetailViewModel.GraphTab;
      var graph = detail.Graph!;
      Assert.AreEqual("RandomCreator", graph.Selected!.Name);
      graph.Selected = graph.Nodes.Single(n => n.Name == "SolutionsCreator");
      var operatorParameters = graph.SelectedEditor!.Nested!.Parameters.Select(p => p.Name).ToList();
      CollectionAssert.IsSubsetOf(new[] { "NumberOfSolutions", "Parallel" }, operatorParameters);
      Assert.IsTrue(graph.SelectedEditor.HasBreakpoint);
      Shot("graph");
      graph.Selected = graph.Nodes.Single(n => n.Name == "TabuSearchMainLoop");
      graph.OpenCommand.Execute(null);
      Assert.IsTrue(graph.CanGoBack);
      Assert.IsTrue(graph.Nodes.Count > 5, "main loop graph");
      graph.BackCommand.Execute(null);
      Assert.IsFalse(graph.CanGoBack);

      // engine
      detail.SelectedTab = AlgorithmDetailViewModel.EngineTab;
      Assert.AreEqual("Sequential Engine", detail.SelectedEngine!.Name);
      Shot("engine");

      // execution: a breakpoint pauses, Start resumes, the run ends up in Results and Runs
      ParameterEditor.Apply(block.Algorithm, ["MaximumIterations=30", "SampleSize=20"]);
      var collector = graph.Nodes.Single(n => n.Name == "ResultsCollector").Operator;
      collector.Breakpoint = true;
      detail.SelectedEngine = detail.Engines.Single(e => e.Name == "Debug Engine");
      Assert.AreEqual("DebugEngine", ((EngineAlgorithm)block.Algorithm).Engine.GetType().Name);
      detail.StartCommand.Execute(null);
      await detail.WaitAsync();
      Assert.AreEqual("Paused", detail.State);
      Assert.IsTrue(ws.IsRunning, "paused counts as running: the tree is locked");
      Assert.IsFalse(detail.CanEdit);
      collector.Breakpoint = false;
      detail.StartCommand.Execute(null);
      await detail.WaitAsync();
      Assert.AreEqual("Stopped", detail.State);
      Assert.IsFalse(ws.IsRunning);
      Assert.IsTrue(detail.Results.Entries.Any(e => e.Name == "BestQuality"), string.Join(", ", detail.Results.Entries.Select(e => e.Name)));
      Assert.AreEqual(1, detail.Runs.Rows.Count);
      Assert.AreEqual("Runs (1)", block.RunsText, "tree counters follow the run");
      Assert.IsTrue(detail.Log.Count > 0, "engine log");
      detail.SelectedTab = AlgorithmDetailViewModel.ResultsTab;
      detail.Results.Selected = detail.Results.Entries.First(e => e.Name == "Qualities");
      Assert.IsTrue(detail.Results.Detail!.HasVisualization);
      Assert.AreEqual("Index", ((ChartPanel)detail.Results.Detail.Visuals.Current!).XAxisTitle, "Qualities has no axis title of its own");
      Shot("results");
      detail.SelectedTab = AlgorithmDetailViewModel.RunsTab;
      detail.Runs.Selected = detail.Runs.Rows[0];
      Assert.IsTrue(detail.Runs.SelectedDetails.Any(d => d.Name == "Parameter: MaximumIterations"));
      Shot("runs");
      window.Close();
      return files;
    }, CancellationToken.None);
    foreach (var f in shots) Assert.IsTrue(new FileInfo(f).Length > 5_000, f);
  }
}
