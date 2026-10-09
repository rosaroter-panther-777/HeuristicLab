using Avalonia;
using Avalonia.Headless;
using HeuristicLab.Studio.ViewModels;
using HeuristicLab.Studio.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Studio.Tests;

public static class TestAppBuilder {
  public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont();
}

/// <summary>
/// Studio view model and window, run on Avalonia's headless UI thread so progress updates are
/// delivered through the dispatcher exactly as in the real application.
/// </summary>
[TestClass]
public class StudioTests {
  private static HeadlessUnitTestSession session = null!;
  private static string LegacyGaTsp => Path.Combine(AppContext.BaseDirectory, "TestData", "GA_TSP.hl");

  [ClassInitialize]
  public static void Start(TestContext _) => session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

  // No ClassCleanup: HeadlessUnitTestSession.Dispose() blocks forever waiting for the dispatcher
  // thread (Avalonia.Headless 12.1.3); the session ends with the test process instead.

  private static Task<T> OnUiThread<T>(Func<Task<T>> action) => session.Dispatch(action, CancellationToken.None);

  [TestMethod]
  public async Task LoadAndRunLegacyFileWithSeed() {
    var (results, parameters, series, status) = await OnUiThread(async () => {
      var vm = new MainViewModel();
      await vm.LoadAsync(LegacyGaTsp);
      vm.SeedText = "0";
      await vm.RunCommand.ExecuteAsync(null);
      return (vm.Results.ToList(), vm.Parameters.ToList(), vm.Series, vm.Status);
    });

    StringAssert.StartsWith(status, "Completed");
    Assert.AreEqual("12332", results.Single(r => r.Name == "CurrentBestQuality").Value);
    Assert.AreEqual("100", parameters.Single(p => p.Name == "PopulationSize").Value);
    Assert.IsTrue(series.Count >= 2, "expected quality curves");
    // after the run the chart shows the algorithm's quality table: one point per generation
    Assert.AreEqual(1001, series.Single(s => s.Name == "BestQuality").Points.Count);
    Assert.AreEqual(12332.0, series.Single(s => s.Name == "BestQuality").Points[^1].Y);
  }

  [TestMethod]
  public async Task InvalidSeedIsRejected() {
    var (status, report) = await OnUiThread(async () => {
      var vm = new MainViewModel();
      await vm.LoadAsync(LegacyGaTsp);
      vm.SeedText = "abc";
      await vm.RunCommand.ExecuteAsync(null);
      return (vm.Status, vm.LastReport);
    });
    StringAssert.Contains(status, "whole number");
    Assert.IsNull(report);
  }

  [TestMethod]
  public async Task CommandsFollowTheDocumentState() {
    var (runBefore, runAfter) = await OnUiThread(async () => {
      var vm = new MainViewModel();
      bool before = vm.RunCommand.CanExecute(null);
      await vm.LoadAsync(LegacyGaTsp);
      return (before, vm.RunCommand.CanExecute(null));
    });
    Assert.IsFalse(runBefore);
    Assert.IsTrue(runAfter);
  }

  [TestMethod]
  public async Task MainWindowRendersAfterARun() {
    // Renders the real window after a seeded run and keeps a screenshot for visual review.
    var path = await OnUiThread(async () => {
      var vm = new MainViewModel();
      var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
      window.Show();
      await vm.LoadAsync(LegacyGaTsp);
      vm.SeedText = "0";
      await vm.RunCommand.ExecuteAsync(null);
      var frame = window.CaptureRenderedFrame();
      Assert.IsNotNull(frame, "headless rendering produced no frame");
      var file = Path.Combine(AppContext.BaseDirectory, "studio-after-run.png");
      frame!.Save(file);
      window.Close();
      return file;
    });
    Assert.IsTrue(new FileInfo(path).Length > 10_000, "screenshot looks empty");
  }
}
