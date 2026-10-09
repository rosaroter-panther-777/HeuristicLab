using Avalonia.Headless;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Optimization;
using HeuristicLab.Studio.Services;
using HeuristicLab.Studio.ViewModels;
using HeuristicLab.Studio.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Studio.Tests;

[TestClass]
public class StudioExperimentBuilderTests {
  private static HeadlessUnitTestSession session = null!;

  [ClassInitialize]
  public static void Start(TestContext _) => session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

  private sealed class FakeDialogs : IDialogService {
    public bool ConfirmAnswer { get; set; } = true;
    public int Confirmations { get; private set; }
    public Task<SetupResult?> NewSetupAsync() => Task.FromResult<SetupResult?>(null);
    public Task<CatalogEntry?> PickAlgorithmAsync() => Task.FromResult(Catalog.Find(Catalog.Algorithms(), "TabuSearch"));
    public Task<IProblem?> PickProblemAsync(IAlgorithm algorithm) => Task.FromResult<IProblem?>(Tsp("berlin52"));
    public Task<bool> ConfirmAsync(string title, string message) { Confirmations++; return Task.FromResult(ConfirmAnswer); }
  }

  private static CatalogEntry TabuSearch => Catalog.Find(Catalog.Algorithms(), "TabuSearch")!;
  private static CatalogEntry Container<T>() => ExperimentTree.ContainerTypes().Single(e => e.Type == typeof(T));

  private static IProblem Tsp(string instance) {
    var scratch = Setups.Create(new SetupRequest("TabuSearch") { Problem = "TravelingSalesmanProblem", Instance = instance }).Algorithm;
    return (IProblem)scratch.Problem!.Clone();
  }

  [TestMethod]
  public async Task MockupStatesAndStructure() {
    var shots = await session.Dispatch(async () => {
      var vm = new MainViewModel(null, new FakeDialogs());
      var ws = vm.Workspace;
      var window = new MainWindow { DataContext = vm, Width = 1150, Height = 760 };
      window.Show();
      var files = new List<string>();
      void Shot(string state) {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var file = Path.Combine(AppContext.BaseDirectory, $"studio-experiments-{state}.png");
        window.CaptureRenderedFrame()!.Save(file);
        files.Add(file);
      }

      // A: empty workspace
      Assert.IsFalse(ws.StartAllCommand.CanExecute(null));
      Shot("a-empty");
      // B: experiment without building blocks
      var experiment = ws.NewExperiment();
      Assert.AreEqual("New experiment 1", experiment.Name);
      Assert.IsFalse(ws.StartAllCommand.CanExecute(null), "nothing to run yet");
      Shot("b-experiment");
      // C: algorithm without problem
      var first = ws.AddAlgorithm(experiment, TabuSearch);
      Assert.IsFalse(first.HasProblem);
      Assert.IsFalse(ws.StartAllCommand.CanExecute(null));
      Shot("c-algorithm");
      // D: algorithm with problem
      await first.ChooseProblemCommand.ExecuteAsync(null);
      Assert.IsTrue(first.HasProblem);
      Assert.AreEqual("TSP: berlin52", first.Problem!.Label);
      Assert.IsTrue(ws.StartAllCommand.CanExecute(null));
      Assert.IsFalse(ws.StartSelectedCommand.CanExecute(null), "no experiment ticked");
      Shot("d-problem");
      // E: empty batch next to the algorithm
      var batch = ws.AddContainer(experiment, Container<BatchRun>());
      Assert.AreEqual("Batch run 1 (10×)", batch.Label);
      Assert.IsFalse(ws.StartAllCommand.CanExecute(null), "empty batch");
      Shot("e-batch");
      // F: three independent algorithms inside the batch, the middle one without problem
      var inBatch = Enumerable.Range(0, 3).Select(_ => ws.AddAlgorithm(batch, TabuSearch)).ToList();
      ws.SetProblem(inBatch[0], Tsp("berlin52"));
      ws.SetProblem(inBatch[2], Tsp("berlin52"));
      Assert.IsFalse(ws.StartAllCommand.CanExecute(null), "an algorithm in the batch has no problem");
      ws.Selected = inBatch[1];
      Shot("f-batch-children");

      // structure: adding to the batch does not add to the experiment root, and vice versa
      Assert.AreEqual(2, experiment.Children.Count);
      Assert.AreEqual(3, batch.Children.Count);
      Assert.AreEqual(2, experiment.Experiment.Optimizers.Count);
      Assert.AreEqual(3, ExperimentTree.Children(batch.Optimizer).Count);
      // each block is its own object: renaming one Tabu Search leaves the others alone
      inBatch[0].Name = "Tabu A";
      Assert.AreNotEqual("Tabu A", inBatch[2].Name);
      Assert.AreNotSame(inBatch[0].Algorithm, inBatch[2].Algorithm);

      // complete it, make it quick and run it
      ws.SetProblem(inBatch[1], Tsp("berlin52"));
      batch.RepetitionsText = "2";
      foreach (var a in ExperimentTree.Algorithms(experiment.Experiment)) ParameterEditor.Apply(a, ["MaximumIterations=20"]);
      Assert.IsTrue(ws.StartAllCommand.CanExecute(null));
      await ws.StartAllCommand.ExecuteAsync(null);
      Assert.AreEqual(1 + 2 * 3, experiment.Experiment.Runs.Count, "1 algorithm + 2 repetitions x 3 algorithms");
      StringAssert.StartsWith(experiment.RunStatus, "Completed: 7 runs");
      Shot("g-completed");
      window.Close();
      return files;
    }, CancellationToken.None);
    foreach (var file in shots) Assert.IsTrue(new FileInfo(file).Length > 5_000, file);
  }

  [TestMethod]
  public async Task DeletingIsScopedAndConfirmedForContainers() {
    await session.Dispatch(async () => {
      var dialogs = new FakeDialogs();
      var ws = new ExperimentWorkspaceViewModel(dialogs);
      var experiment = ws.NewExperiment();
      var algorithm = ws.AddAlgorithm(experiment, TabuSearch);
      ws.SetProblem(algorithm, Tsp("berlin52"));
      var timeLimit = ws.AddContainer(experiment, Container<TimeLimitRun>());
      Assert.AreEqual(1, timeLimit.AddChoices.Count, "a time-limit run only takes an algorithm");
      ws.AddAlgorithm(timeLimit, TabuSearch);
      Assert.IsFalse(timeLimit.CanAdd, "a time-limit run holds one algorithm");

      // removing a problem only clears that algorithm's problem
      await algorithm.Problem!.DeleteCommand.ExecuteAsync(null);
      Assert.IsNull(algorithm.Algorithm.Problem);
      Assert.AreEqual(2, experiment.Children.Count);

      ws.SetProblem(algorithm, Tsp("berlin52"));
      ParameterEditor.Apply(algorithm.Algorithm, ["MaximumIterations=5"]);
      var report = await OptimizerRunner.RunAsync(algorithm.Algorithm);
      Assert.AreEqual(RunOutcome.Completed, report.Outcome, "runs after its problem was removed and set again: " + report.Error);

      // a container with children asks first
      dialogs.ConfirmAnswer = false;
      await timeLimit.DeleteCommand.ExecuteAsync(null);
      Assert.AreEqual(2, experiment.Children.Count);
      dialogs.ConfirmAnswer = true;
      await timeLimit.DeleteCommand.ExecuteAsync(null);
      Assert.AreEqual(1, experiment.Children.Count);
      Assert.AreEqual(1, experiment.Experiment.Optimizers.Count);
      Assert.AreEqual(2, dialogs.Confirmations);

      // deleting an algorithm without children does not ask
      await algorithm.DeleteCommand.ExecuteAsync(null);
      Assert.AreEqual(2, dialogs.Confirmations);
      Assert.AreEqual(0, experiment.Experiment.Optimizers.Count);
    }, CancellationToken.None);
  }

  [TestMethod]
  public async Task StartSelectedRunsOnlyTickedExperimentsAndFilesRoundTrip() {
    var dir = Path.Combine(Path.GetTempPath(), $"studio-experiments-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      await session.Dispatch(async () => {
        var ws = new ExperimentWorkspaceViewModel(new FakeDialogs());
        ExperimentBlockViewModel Build() {
          var e = ws.NewExperiment();
          var a = ws.AddAlgorithm(e, TabuSearch);
          ws.SetProblem(a, Tsp("berlin52"));
          ParameterEditor.Apply(a.Algorithm, ["MaximumIterations=10"]);
          return e;
        }
        var one = Build();
        var two = Build();
        two.IsChecked = true;
        Assert.IsTrue(ws.StartSelectedCommand.CanExecute(null));
        await ws.StartSelectedCommand.ExecuteAsync(null);
        Assert.AreEqual(0, one.Experiment.Runs.Count);
        Assert.AreEqual(1, two.Experiment.Runs.Count);

        // save and reopen: the tree comes back
        var batch = ws.AddContainer(two, Container<BatchRun>());
        ws.AddAlgorithm(batch, TabuSearch);
        var path = Path.Combine(dir, "two.hl");
        Documents.Save(two.Experiment, path);
        var reopened = await ws.OpenExperimentAsync(path);
        Assert.AreEqual(3, ws.Experiments.Count);
        Assert.AreEqual(2, reopened!.Children.Count);
        Assert.AreEqual(1, ((ContainerBlockViewModel)reopened.Children[1]).Children.Count);
        Assert.AreEqual(path, reopened.FilePath);

        // a file with a single algorithm opens inside a new experiment
        var wrapped = await ws.OpenExperimentAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "GA_TSP.hl"));
        Assert.IsInstanceOfType<AlgorithmBlockViewModel>(wrapped!.Children.Single());
        Assert.IsNull(wrapped.FilePath, "saving must not overwrite the algorithm file with an experiment");
      }, CancellationToken.None);
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
