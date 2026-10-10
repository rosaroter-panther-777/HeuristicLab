using Avalonia.Headless;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Optimization;
using HeuristicLab.Studio.Controls;
using HeuristicLab.Studio.ViewModels;
using HeuristicLab.Studio.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Studio.Tests;

/// <summary>Building blocks as squares with pictograms: kinds, saved state, algorithm status, counters while running.</summary>
[TestClass]
public class StudioBlockDesignTests {
  private static HeadlessUnitTestSession session = null!;

  [ClassInitialize]
  public static void Start(TestContext _) => session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

  private static CatalogEntry TabuSearch => Catalog.Find(Catalog.Algorithms(), "TabuSearch")!;
  private static CatalogEntry Container<T>() => ExperimentTree.ContainerTypes().Single(e => e.Type == typeof(T));

  private static IProblem Tsp() {
    var scratch = Setups.Create(new SetupRequest("TabuSearch") { Problem = "TravelingSalesmanProblem", Instance = "berlin52" }).Algorithm;
    return (IProblem)scratch.Problem!.Clone();
  }

  private static async Task Until(Func<bool> condition, string what, int seconds = 60) {
    var clock = System.Diagnostics.Stopwatch.StartNew();
    while (!condition()) {
      if (clock.Elapsed.TotalSeconds > seconds) Assert.Fail($"Timed out waiting for: {what}");
      await Task.Delay(20);
    }
  }

  [TestMethod]
  public async Task EveryPictogramLoads() {
    await session.Dispatch(() => {
      var folder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "app", "HeuristicLab.Studio", "Assets", "Pictograms");
      var symbols = Directory.GetFiles(folder, "*.svg").Select(Path.GetFileNameWithoutExtension).ToList();
      Assert.IsTrue(symbols.Count >= 20, folder);
      foreach (var symbol in symbols) {
        var drawing = Pictogram.Load(symbol!);
        Assert.IsNotNull(drawing, symbol);
        Assert.IsTrue(drawing.Parts.Count > 0, symbol);
        Assert.AreEqual(24, drawing.ViewBox.Width, symbol);
      }
      Assert.IsTrue(Pictogram.Load("Algorithm_status_working")!.SpinSeconds > 0, "the working status turns");
      Assert.AreEqual(0, Pictogram.Load("Algorithm_status_succesfull_finished")!.SpinSeconds);
      Assert.IsNotNull(Pictogram.Load("experiment_saved")!.Parts[0].Fill, "saved/unsaved keep their own colors");
      Assert.IsNull(Pictogram.Load("Algorith_logo")!.Parts[0].Fill, "plain pictograms use the foreground");
      Assert.IsNull(Pictogram.Load("no such pictogram"));
      Assert.AreEqual(0, Pictogram.Angle(0, 1.2), 1e-9);
      Assert.AreEqual(-90, Pictogram.Angle(0.3, 1.2), 1e-9);
      Assert.AreEqual(-45, Pictogram.Angle(0.15, 1.2), 1e-9);
      return Task.CompletedTask;
    }, CancellationToken.None);
  }

  [TestMethod]
  public async Task ParameterKindsAndNumberedAnalyzers() {
    var shot = await session.Dispatch(async () => {
      var vm = new MainViewModel(null);
      var ws = vm.Workspace;
      var experiment = ws.NewExperiment();
      var block = ws.AddAlgorithm(experiment, TabuSearch);
      ws.SetProblem(block, Tsp());
      var context = new EditContext(_ => { }, () => true);

      var algorithm = new ParameterListViewModel(block.Algorithm, context);
      Assert.AreEqual("value_parameter", algorithm.Parameters.First(p => p.Name == "MaximumIterations").KindPictogram);
      Assert.AreEqual("constrained_value_parameter", algorithm.Parameters.First(p => p.Name == "MoveGenerator").KindPictogram);
      var problem = new ParameterListViewModel((HeuristicLab.Core.IParameterizedItem)block.Algorithm.Problem!, context);
      Assert.AreEqual("optional_value_parameter", problem.Parameters.First(p => p.Name == "BestKnownSolution").KindPictogram);

      // the analyzers: numbered in execution order, a heading above the selected one's options
      var analyzers = algorithm.Parameters.First(p => p.Name == "Analyzer").Editor;
      Assert.IsTrue(analyzers.IsCheckedList);
      CollectionAssert.AreEqual(Enumerable.Range(1, analyzers.Entries.Count).Select(i => $"{i}.").ToList(), analyzers.Entries.Select(e => e.Number).ToList());
      foreach (var entry in analyzers.Entries) {
        analyzers.SelectedEntry = entry;
        Assert.AreEqual($"{entry.Name} selected. " + (entry.Parameters == null ? "It has no options." : "Details/Options:"), analyzers.SelectedEntryText);
      }
      var second = analyzers.Entries.Skip(1).First(e => e.Parameters != null);
      analyzers.SelectedEntry = second;
      string moved = second.Name;
      while (analyzers.MoveUpCommand.CanExecute(null) && analyzers.SelectedEntry!.Index > 1) analyzers.MoveUpCommand.Execute(null);
      analyzers.MoveUpCommand.Execute(null);
      Assert.AreEqual(moved, analyzers.Entries[0].Name, "moved to the top");
      Assert.AreEqual("1.", analyzers.Entries[0].Number);
      var nested = analyzers.SelectedEntry!.Parameters!;
      StringAssert.EndsWith(nested.SelectedText, " selected. Details/Options:");

      // the algorithm tab with the analyzer selected
      var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
      window.Show();
      ws.Selected = block;
      block.Detail.SelectedTab = AlgorithmDetailViewModel.AlgorithmTab;
      Avalonia.Threading.Dispatcher.UIThread.RunJobs();
      var list = window.GetVisualDescendantsOf<ParameterListView>().First();
      var shown = (ParameterListViewModel)list.DataContext!;
      shown.Selected = shown.Parameters.First(p => p.Name == "Analyzer");
      Avalonia.Threading.Dispatcher.UIThread.RunJobs();
      shown.Selected.Editor.SelectedEntry = shown.Selected.Editor.Entries[0];
      Avalonia.Threading.Dispatcher.UIThread.RunJobs();
      Assert.IsTrue(window.GetVisualDescendantsOf<Pictogram>().Any(p => p.Symbol == "constrained_value_parameter"));
      var file = Path.Combine(AppContext.BaseDirectory, "studio-analyzers.png");
      window.CaptureRenderedFrame()!.Save(file);
      window.Close();
      await Task.CompletedTask;
      return file;
    }, CancellationToken.None);
    Assert.IsTrue(new FileInfo(shot).Length > 5_000);
  }

  [TestMethod]
  public async Task ResultKindPictograms() {
    var shot = await session.Dispatch(async () => {
      var vm = new MainViewModel(null);
      var ws = vm.Workspace;
      var block = ws.AddAlgorithm(ws.NewExperiment(), TabuSearch);
      ws.SetProblem(block, Tsp());
      ParameterEditor.Apply(block.Algorithm, ["MaximumIterations=20"]);
      await OptimizerRunner.RunAsync(block.Algorithm);
      var detail = block.ShowDetail(AlgorithmDetailViewModel.ResultsTab);
      detail.Results.Refresh();
      string Kind(string name) => detail.Results.Entries.FirstOrDefault(e => e.Name == name)?.KindPictogram ?? throw new AssertFailedException(name + " not in " + string.Join(", ", detail.Results.Entries.Select(e => e.Name)));
      Assert.AreEqual("result_diagram_logo", Kind("Qualities"), "a table is a chart");
      Assert.AreEqual("result_visualisation_logo", Kind("Best TSP Solution"), "a tour is a picture");
      Assert.AreEqual("optional_value_parameter", Kind("BestQuality"), "a number is a value");

      var window = new MainWindow { DataContext = vm, Width = 1300, Height = 760 };
      window.Show();
      ws.Selected = block;
      block.Detail.SelectedTab = AlgorithmDetailViewModel.ResultsTab;
      Avalonia.Threading.Dispatcher.UIThread.RunJobs();
      Assert.IsTrue(window.GetVisualDescendantsOf<Pictogram>().Any(p => p.Symbol == "result_diagram_logo"));
      var file = Path.Combine(AppContext.BaseDirectory, "studio-result-kinds.png");
      window.CaptureRenderedFrame()!.Save(file);
      window.Close();
      return file;
    }, CancellationToken.None);
    Assert.IsTrue(new FileInfo(shot).Length > 5_000);
  }

  [TestMethod]
  public async Task SavedStateFollowsChanges() {
    await session.Dispatch(async () => {
      var ws = new ExperimentWorkspaceViewModel();
      var fresh = ws.NewExperiment();
      Assert.IsFalse(fresh.IsSaved);
      Assert.AreEqual("experiment_unsaved", fresh.StatePictogram);
      Assert.AreEqual("experiment_logo", fresh.Pictogram);

      var experiment = new Experiment { Name = "Saved one" };
      var algorithm = (IAlgorithm)TabuSearch.CreateInstance();
      algorithm.Problem = Tsp();
      experiment.Optimizers.Add(algorithm);
      var path = Path.Combine(Path.GetTempPath(), $"studio-saved-{Guid.NewGuid():N}.hl");
      try {
        Documents.Save(experiment, path);
        var opened = (await ws.OpenExperimentAsync(path))!;
        Assert.IsTrue(opened.IsSaved, "just opened from its file");
        Assert.AreEqual("experiment_saved", opened.StatePictogram);
        var block = (AlgorithmBlockViewModel)opened.Children[0];
        Assert.AreEqual("Algorith_logo", block.Pictogram);
        Assert.AreEqual("Problem_logo", block.Problem!.Pictogram);
        block.Name = "Renamed";
        Assert.IsFalse(opened.IsSaved, "renaming a block changes the experiment");
        Assert.AreEqual("experiment_unsaved", opened.StatePictogram);
        StringAssert.Contains(opened.StateTip, "Changed since saved");

        // an edit in the algorithm's parameters counts too
        opened.IsModified = false;
        var parameters = new ParameterListViewModel(block.Algorithm, new EditContext(_ => { }, () => true, block.MarkModified));
        var node = parameters.Parameters.First(p => p.Name == "MaximumIterations");
        node.Editor.Text = "123";
        Assert.IsFalse(opened.IsSaved, "a parameter edit changes the experiment");
      } finally {
        File.Delete(path);
      }
    }, CancellationToken.None);
  }

  [TestMethod]
  public async Task StatusAndCountersWhileRunning() {
    var shot = await session.Dispatch(async () => {
      var vm = new MainViewModel(null);
      var ws = vm.Workspace;
      var window = new MainWindow { DataContext = vm, Width = 1300, Height = 760 };
      window.Show();

      var experiment = ws.NewExperiment();
      var batch = ws.AddContainer(experiment, Container<BatchRun>());
      batch.RepetitionsText = "2";
      var inBatch = ws.AddAlgorithm(batch, TabuSearch);
      Assert.AreEqual(AlgorithmStatus.ProblemMissing, inBatch.Status);
      Assert.AreEqual("Algorithm_status_Problem_occured", inBatch.StatePictogram);
      ws.SetProblem(inBatch, Tsp());
      ParameterEditor.Apply(inBatch.Algorithm, ["MaximumIterations=150"]);
      Assert.AreEqual(AlgorithmStatus.NotRun, inBatch.Status);
      Assert.AreEqual("Algorithm_never_attempted_or_aborted", inBatch.StatePictogram);

      var timeLimit = ws.AddContainer(experiment, Container<TimeLimitRun>());
      timeLimit.TimeLimitText = "2s";
      var limited = ws.AddAlgorithm(timeLimit, TabuSearch);
      ws.SetProblem(limited, Tsp());
      ParameterEditor.Apply(limited.Algorithm, ["MaximumIterations=100000000"]);
      Assert.AreEqual("2×", batch.Badge);
      Assert.AreEqual("2 s", timeLimit.Badge);
      Assert.AreEqual("batch_run", batch.Pictogram);
      Assert.AreEqual("time_limit_run", timeLimit.Pictogram);

      var run = ws.StartAllCommand.ExecuteAsync(null);
      // the experiment runs its blocks one after another: the batch first, the time-limit run waits
      await Until(() => inBatch.Status == AlgorithmStatus.Working, "batch algorithm working");
      Assert.AreEqual("Algorithm_status_working", inBatch.StatePictogram);
      Assert.AreEqual(AlgorithmStatus.Waiting, limited.Status, "waits for its turn");
      StringAssert.Matches(batch.Badge, new System.Text.RegularExpressions.Regex(@"^[12] / 2$"));
      await Until(() => limited.Status == AlgorithmStatus.Working && limited.Algorithm.ExecutionTime > TimeSpan.FromSeconds(1), "time-limit algorithm working");
      StringAssert.Matches(timeLimit.Badge, new System.Text.RegularExpressions.Regex(@"^00:0[0-2] / 00:02$"));
      Assert.AreEqual(AlgorithmStatus.Finished, inBatch.Status);
      Avalonia.Threading.Dispatcher.UIThread.RunJobs();
      var spinner = window.GetVisualDescendantsOf<Pictogram>().FirstOrDefault(p => p.Symbol == "Algorithm_status_working");
      Assert.IsNotNull(spinner, "the working pictogram is shown");
      Assert.IsTrue(spinner.IsSpinning);
      var file = Path.Combine(AppContext.BaseDirectory, "studio-blocks-running.png");
      window.CaptureRenderedFrame()!.Save(file);

      await run;
      ws.RefreshLive();
      Assert.AreEqual(AlgorithmStatus.Finished, limited.Status, "stopped by its time limit: finished, not aborted");
      Assert.AreEqual("Algorithm_status_succesfull_finished", limited.StatePictogram);
      Assert.AreEqual("2×", batch.Badge, "back to the setting");
      Assert.AreEqual("2 s", timeLimit.Badge);
      Assert.IsFalse(experiment.IsSaved, "new runs are not saved yet");
      Avalonia.Threading.Dispatcher.UIThread.RunJobs();
      window.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "studio-blocks-finished.png"));

      // stopped by the user: not finished
      timeLimit.TimeLimitText = "5m";
      run = ws.StartAllCommand.ExecuteAsync(null);
      await Until(() => limited.Status == AlgorithmStatus.Working, "working again");
      ws.StopCommand.Execute(null);
      await run;
      Avalonia.Threading.Dispatcher.UIThread.RunJobs();
      Assert.AreEqual(AlgorithmStatus.NotRun, limited.Status);
      Assert.AreEqual("Stopped before it finished", limited.StateTip);
      window.Close();
      return file;
    }, CancellationToken.None);
    Assert.IsTrue(new FileInfo(shot).Length > 5_000);
  }
}

internal static class VisualTreeExtensions {
  public static IEnumerable<T> GetVisualDescendantsOf<T>(this Avalonia.Visual root) =>
    Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>();
}
