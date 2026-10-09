using Avalonia.Controls;
using Avalonia.Headless;
using HeuristicLab.Algorithms.GeneticAlgorithm;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Problems.TravelingSalesman;
using HeuristicLab.Studio.ViewModels;
using HeuristicLab.Studio.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Studio.Tests;

[TestClass]
public class StudioWorkflowTests {
  private static HeadlessUnitTestSession session = null!;

  [ClassInitialize]
  public static void Start(TestContext _) => session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
  // no ClassCleanup: HeadlessUnitTestSession.Dispose() hangs (see StudioTests)

  private static Task<T> OnUiThread<T>(Func<Task<T>> action) => session.Dispatch(action, CancellationToken.None);
  private static Task<T> OnUiThread<T>(Func<T> action) => session.Dispatch(() => Task.FromResult(action()), CancellationToken.None);

  private static string Shot(string name) => Path.Combine(AppContext.BaseDirectory, name);

  private static SetupResult SmallGaTsp() => Setups.Create(new SetupRequest("GeneticAlgorithm") {
    Problem = "TravelingSalesmanProblem", Instance = "berlin52", Settings = ["PopulationSize=20", "MaximumGenerations=10"]
  });

  [TestMethod]
  public async Task NewDialogBuildsGaForBenchmarkInstance() {
    var (created, instances, file) = await OnUiThread(() => {
      var vm = new NewSetupViewModel();
      vm.SelectedAlgorithm = vm.Algorithms.Single(a => a.Entry?.Type == typeof(GeneticAlgorithm));
      vm.SelectedProblem = vm.Problems.Single(p => p.Entry?.Type == typeof(TravelingSalesmanProblem));
      vm.SelectedInstance = vm.Instances.Single(i => i.EndsWith("/berlin52"));
      var window = new NewSetupWindow { DataContext = vm };
      window.Show();
      var frame = window.CaptureRenderedFrame();
      var path = Shot("studio-new-dialog.png");
      frame!.Save(path);
      window.Close();
      return (vm.Create() ? vm.Result : null, vm.Instances.Count, path);
    });
    Assert.IsNotNull(created);
    Assert.AreEqual(52, ((TravelingSalesmanProblem)((GeneticAlgorithm)created.Algorithm).Problem).Coordinates.Rows);
    Assert.IsTrue(instances > 100);
    Assert.IsTrue(new FileInfo(file).Length > 5_000);
  }

  [TestMethod]
  public async Task NewDialogLoadsDataFileForRegression() {
    var csv = Path.Combine(Path.GetTempPath(), $"studio-data-{Guid.NewGuid():N}.csv");
    File.WriteAllLines(csv, new[] { "x1,x2,y" }.Concat(Enumerable.Range(0, 40).Select(i => FormattableString.Invariant($"{i},{i % 7},{2 * i + i % 7}"))));
    try {
      var (ok, error, columns, accepts) = await OnUiThread(() => {
        var vm = new NewSetupViewModel();
        vm.SelectedAlgorithm = vm.Algorithms.Single(a => a.Display == "Linear Regression (LR)");
        vm.SelectedProblem = vm.Problems.Single(p => p.Display == "Regression Problem");
        bool acceptsData = vm.AcceptsData;
        vm.DataFile = csv;
        var cols = vm.Columns.ToList();
        vm.Target = "y";
        return (vm.Create(), vm.Error, cols, acceptsData);
      });
      Assert.IsTrue(accepts);
      CollectionAssert.AreEqual(new[] { "x1", "x2", "y" }, columns);
      Assert.IsTrue(ok, error);
    } finally { File.Delete(csv); }
  }

  [TestMethod]
  public async Task NewDialogRejectsInstanceAndDataTogether() {
    var csv = Path.Combine(Path.GetTempPath(), $"studio-data-{Guid.NewGuid():N}.csv");
    File.WriteAllLines(csv, ["x,y", "1,2", "2,4", "3,6"]);
    try {
      var error = await OnUiThread(() => {
        var vm = new NewSetupViewModel();
        vm.SelectedAlgorithm = vm.Algorithms.Single(a => a.Display == "Linear Regression (LR)");
        vm.SelectedProblem = vm.Problems.Single(p => p.Display == "Regression Problem");
        vm.SelectedInstance = vm.Instances.First(i => i != "(none)");
        vm.DataFile = csv;
        return vm.Create() ? null : vm.Error;
      });
      StringAssert.Contains(error, "either a benchmark instance or a data file");
    } finally { File.Delete(csv); }
  }

  [TestMethod]
  public async Task ParametersAreEditableAndInvalidValuesAreReverted() {
    var (population, selector, status, shown) = await OnUiThread(() => {
      var vm = new MainViewModel();
      var setup = SmallGaTsp();
      vm.ShowNew(setup);
      vm.Parameters.Single(p => p.Name == "PopulationSize").Value = "35";
      vm.Parameters.Single(p => p.Name == "Selector").Value = "TournamentSelector";
      var bad = vm.Parameters.Single(p => p.Name == "MaximumGenerations");
      bad.Value = "lots";
      var ga = (GeneticAlgorithm)setup.Algorithm;
      return (ga.PopulationSize.Value, ga.Selector.GetType().Name, vm.Status, bad.Value);
    });
    Assert.AreEqual(35, population);
    Assert.AreEqual("TournamentSelector", selector);
    StringAssert.Contains(status, "lots");
    Assert.AreEqual("10", shown);
  }

  [TestMethod]
  public async Task BatchRunStoresResultsAndBrowserSummarizesThem() {
    var folder = Path.Combine(Path.GetTempPath(), $"studio-store-{Guid.NewGuid():N}");
    try {
      var (completed, stored, results, runs, summary, mainShot, resultsShot) = await OnUiThread(async () => {
        var vm = new MainViewModel();
        vm.ShowNew(SmallGaTsp());
        vm.SeedText = "3";
        vm.RepetitionsText = "4";
        vm.ParallelText = "2";
        vm.ResultsFolder = folder;
        var window = new MainWindow { DataContext = vm, Width = 1150, Height = 720 };
        window.Show();
        await vm.RunCommand.ExecuteAsync(null);
        var main = Shot("studio-batch.png");
        window.CaptureRenderedFrame()!.Save(main);

        vm.ResultsBrowser.Load(folder);
        window.GetVisualDescendantsOfType<TabControl>().Single().SelectedIndex = 1;
        var browser = Shot("studio-results.png");
        window.CaptureRenderedFrame()!.Save(browser);
        window.Close();
        return (vm.LastBatch!.Completed, Directory.GetFiles(folder, "*.json").Length, vm.Results.Select(r => r.Name).ToList(),
                vm.ResultsBrowser.Runs.Count, vm.ResultsBrowser.Summary.ToList(), main, browser);
      });
      Assert.AreEqual(4, completed);
      Assert.AreEqual(4, stored);
      CollectionAssert.Contains(results, "BestQuality");
      Assert.AreEqual(4, runs);
      Assert.AreEqual(4, summary.Single().Count);
      Assert.IsTrue(new FileInfo(mainShot).Length > 10_000 && new FileInfo(resultsShot).Length > 10_000);
    } finally {
      if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
  }
}

internal static class VisualExtensions {
  public static IEnumerable<T> GetVisualDescendantsOfType<T>(this Avalonia.Visual root) where T : Avalonia.Visual =>
    Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>();
}
