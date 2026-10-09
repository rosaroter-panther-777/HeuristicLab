using Avalonia.Controls;
using Avalonia.Headless;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Studio.ViewModels;
using HeuristicLab.Studio.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Studio.Tests;

[TestClass]
public class StudioSolutionTests {
  private static HeadlessUnitTestSession session = null!;

  [ClassInitialize]
  public static void Start(TestContext _) => session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

  private static string Temp(string ext) => Path.Combine(Path.GetTempPath(), $"studio-sol-{Guid.NewGuid():N}{ext}");

  /// <summary>Runs the setup in the real window and captures the Solution tab.</summary>
  private static Task<(SolutionKind? Kind, string? Model, int Metrics, int Markers, string Shot)> RunAndShow(SetupResult setup, string shotName) =>
    session.Dispatch(async () => {
      var vm = new MainViewModel();
      vm.ShowNew(setup);
      vm.SeedText = "1";
      var window = new MainWindow { DataContext = vm, Width = 1150, Height = 720 };
      window.Show();
      await vm.RunCommand.ExecuteAsync(null);
      window.GetVisualDescendantsOfType<TabControl>().Single().SelectedIndex = 1;
      var shot = Path.Combine(AppContext.BaseDirectory, shotName);
      window.CaptureRenderedFrame()!.Save(shot);
      window.Close();
      var s = vm.Solution;
      return (s.Selected?.Kind, s.Model, s.Metrics.Count, s.Markers.Count, shot);
    }, CancellationToken.None);

  [TestMethod]
  public async Task TradingSolutionShowsEquityAndTestMarker() {
    var random = new System.Random(3);
    var close = new List<double> { 100 };
    for (int t = 1; t < 400; t++) close.Add(close[^1] * Math.Exp((random.NextDouble() - 0.5) * 0.03));
    var path = Temp(".parquet");
    DataFiles.Write(Features.Derive(new TabularData(["Close"], [close]), ["diff(Close)", "lag(logreturn(Close),1..3)"], dropIncomplete: true), path);
    try {
      var setup = Setups.Create(new SetupRequest("GeneticAlgorithm") {
        Problem = "Symbolic Trading Problem (single-objective)", DataFile = path, Target = "diff(Close)",
        Settings = ["PopulationSize=100", "MaximumGenerations=10"]
      });
      var (kind, model, metrics, markers, shot) = await RunAndShow(setup, "studio-solution-trading.png");
      Assert.AreEqual(SolutionKind.Trading, kind);
      Assert.IsFalse(string.IsNullOrEmpty(model));
      Assert.IsTrue(metrics >= 4);
      Assert.AreEqual(1, markers);
      Assert.IsTrue(new FileInfo(shot).Length > 10_000);
    } finally { File.Delete(path); }
  }

  [TestMethod]
  public async Task RegressionSolutionShowsActualVsPredicted() {
    var path = Temp(".csv");
    var random = new System.Random(4);
    File.WriteAllLines(path, new[] { "x,y" }.Concat(Enumerable.Range(0, 120).Select(i =>
      FormattableString.Invariant($"{i * 0.1},{Math.Sin(i * 0.1) * 3 + i * 0.05 + (random.NextDouble() - 0.5) * 0.4}"))));
    try {
      var setup = Setups.Create(new SetupRequest("Linear Regression (LR)") { Problem = "Regression Problem", DataFile = path, Target = "y", TrainingPercent = 70 });
      var (kind, model, _, markers, shot) = await RunAndShow(setup, "studio-solution-regression.png");
      Assert.AreEqual(SolutionKind.Regression, kind);
      StringAssert.Contains(model, "x");
      Assert.AreEqual(1, markers);
      Assert.IsTrue(new FileInfo(shot).Length > 10_000);
    } finally { File.Delete(path); }
  }

  [TestMethod]
  public async Task TspSolutionShowsTour() {
    var setup = Setups.Create(new SetupRequest("GeneticAlgorithm") {
      Problem = "TravelingSalesmanProblem", Instance = "berlin52", Settings = ["PopulationSize=100", "MaximumGenerations=300"]
    });
    var (kind, _, metrics, markers, shot) = await RunAndShow(setup, "studio-solution-tsp.png");
    Assert.AreEqual(SolutionKind.Route, kind);
    Assert.AreEqual(1, metrics);
    Assert.AreEqual(0, markers);
    Assert.IsTrue(new FileInfo(shot).Length > 10_000);
  }
}
