using Avalonia.Controls;
using Avalonia.Headless;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Studio.ViewModels;
using HeuristicLab.Studio.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Studio.Tests;

[TestClass]
public class StudioExperimentTests {
  private static HeadlessUnitTestSession session = null!;

  [ClassInitialize]
  public static void Start(TestContext _) => session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

  [TestMethod]
  public async Task SweepComparesConfigurationsAndBrowserGroupsByConfig() {
    var folder = Path.Combine(Path.GetTempPath(), $"studio-sweep-{Guid.NewGuid():N}");
    try {
      var (results, groups, groupBy) = await session.Dispatch(async () => {
        var vm = new MainViewModel();
        vm.ShowNew(Setups.Create(new SetupRequest("GeneticAlgorithm") {
          Problem = "TravelingSalesmanProblem", Instance = "berlin52", Settings = ["MaximumGenerations=10"] }));
        vm.SweepText = "PopulationSize=10,20; Selector=TournamentSelector,ProportionalSelector";
        vm.RepetitionsText = "2";
        vm.ParallelText = "2";
        vm.SeedText = "1";
        vm.ResultsFolder = folder;
        await vm.RunCommand.ExecuteAsync(null);
        vm.ResultsBrowser.Load(folder);
        return (vm.Results.Select(r => r.Name).ToList(), vm.ResultsBrowser.Summary.Count, vm.ResultsBrowser.GroupBy);
      }, CancellationToken.None);
      Assert.AreEqual(4, results.Count);
      CollectionAssert.Contains(results, "PopulationSize=20; Selector=ProportionalSelector");
      Assert.AreEqual("label:config", groupBy);
      Assert.AreEqual(4, groups);
    } finally {
      if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
  }

  [TestMethod]
  public async Task WalkForwardShowsTestMetricsPerFold() {
    var random = new System.Random(8);
    var close = new List<double> { 100 };
    for (int t = 1; t < 400; t++) close.Add(close[^1] * Math.Exp((random.NextDouble() - 0.5) * 0.03));
    var path = Path.Combine(Path.GetTempPath(), $"studio-wf-{Guid.NewGuid():N}.parquet");
    DataFiles.Write(Features.Derive(new TabularData(["Close"], [close]), ["diff(Close)", "lag(logreturn(Close),1..3)"], dropIncomplete: true), path);
    try {
      var (results, points, status, shot) = await session.Dispatch(async () => {
        var vm = new MainViewModel();
        vm.ShowNew(Setups.Create(new SetupRequest("GeneticAlgorithm") {
          Problem = "Symbolic Trading Problem (single-objective)", DataFile = path, Target = "diff(Close)",
          Settings = ["PopulationSize=50", "MaximumGenerations=5"] }));
        vm.WalkTrainText = "150";
        vm.WalkTestText = "50";
        vm.SeedText = "1";
        vm.ParallelText = "4";
        var window = new MainWindow { DataContext = vm, Width = 1150, Height = 720 };
        window.Show();
        await vm.RunCommand.ExecuteAsync(null);
        var file = Path.Combine(AppContext.BaseDirectory, "studio-walkforward.png");
        window.CaptureRenderedFrame()!.Save(file);
        window.Close();
        return (vm.Results.Select(r => r.Name).ToList(), vm.Series.Single().Points.Count, vm.Status, file);
      }, CancellationToken.None);
      StringAssert.StartsWith(status, "Walk-forward finished: 4 folds");
      CollectionAssert.Contains(results, "Sharpe ratio (test)");
      Assert.AreEqual(4, points);
      Assert.IsTrue(new FileInfo(shot).Length > 10_000);
    } finally { File.Delete(path); }
  }
}
