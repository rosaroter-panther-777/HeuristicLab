using Avalonia.Headless;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Next.Runtime.Visuals;
using HeuristicLab.Optimization;
using HeuristicLab.Studio.ViewModels;
using HeuristicLab.Studio.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Studio.Tests;

/// <summary>The detailed analysis tab: record a run, select iterations and ranges, step through steps.</summary>
[TestClass]
public class StudioAnalysisTests {
  private static HeadlessUnitTestSession session = null!;

  [ClassInitialize]
  public static void Start(TestContext _) => session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

  private static async Task<(MainWindow Window, AlgorithmDetailViewModel Detail)> Open(string algorithm, string problem, string? instance, params string[] settings) {
    var vm = new MainViewModel(null, null);
    var ws = vm.Workspace;
    var window = new MainWindow { DataContext = vm, Width = 1440, Height = 960 };
    window.Show();
    var experiment = ws.NewExperiment();
    var block = ws.AddAlgorithm(experiment, Catalog.Find(Catalog.Algorithms(), algorithm)!);
    var setup = Setups.Create(new SetupRequest(algorithm) { Problem = problem, Instance = instance });
    ws.SetProblem(block, (IProblem)setup.Algorithm.Problem!.Clone());
    ParameterEditor.Apply(block.Algorithm, settings);
    ws.Selected = block;
    var detail = (AlgorithmDetailViewModel)ws.Detail!;
    detail.SelectedTab = AlgorithmDetailViewModel.AnalysisTab;
    await Task.Yield();
    return (window, detail);
  }

  private static void Shot(MainWindow window, string name) {
    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    window.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, $"studio-analysis-{name}.png"));
  }

  [TestMethod]
  [Timeout(300_000)]
  public async Task WalkThroughAGeneticAlgorithmOnTheTsp() {
    await session.Dispatch(async () => {
      var (window, detail) = await Open("GeneticAlgorithm", "TravelingSalesmanProblem", "TSPLIB (symmetric TSP)/ch130", "PopulationSize=30", "MaximumGenerations=40");
      var analysis = detail.Analysis;
      Assert.IsTrue(analysis.IsSupported);
      await analysis.StartAsync();
      StringAssert.StartsWith(analysis.Status, "Finished");
      Assert.AreEqual(0, detail.Algorithm.Runs.Count, "the copy ran, not the algorithm itself");
      Assert.AreEqual(4, analysis.Series.Count);
      Assert.AreEqual(41, analysis.Series[0].Points.Count, "best so far for the initialization and 40 generations");
      Assert.IsTrue(analysis.TimeAxis.Count > 2, "elapsed time along the top");

      // click on iteration 20: its best solution, with five earlier iterations fading underneath
      analysis.SelectionStart = 20;
      Assert.AreEqual(20, analysis.CurrentIteration);
      var scene = (SceneVisual)analysis.Visuals.Current!;
      var tours = scene.Shapes.OfType<PathShape>().ToList();
      Assert.IsTrue(tours.Count >= 6, $"current tour, parents and earlier iterations ({tours.Count})");
      Assert.AreEqual(255, tours[^1].Color.A, "the current tour is drawn opaque, on top");
      Assert.IsTrue(tours.Take(tours.Count - 1).All(t => t.Color.A < 255), "earlier ones fade");
      StringAssert.Contains(analysis.StepText, "created #");
      Shot(window, "iteration");

      // steps and iterations back and forth
      int step = analysis.CurrentStep;
      analysis.NextStepCommand.Execute(null);
      Assert.IsTrue(analysis.CurrentStep == step + 1 || analysis.CurrentIteration == 21);
      analysis.PreviousStepCommand.Execute(null);
      Assert.AreEqual((20, step), (analysis.CurrentIteration, analysis.CurrentStep));
      analysis.NextIterationCommand.Execute(null);
      Assert.AreEqual(21, analysis.CurrentIteration);
      Assert.AreEqual(21.0, analysis.SelectionStart, "the chart's line follows");
      analysis.PreviousIterationCommand.Execute(null);
      Assert.AreEqual(20, analysis.CurrentIteration);

      // a range: every iteration in it is drawn, fading with age; the summary covers the range
      analysis.SelectionStart = 10;
      analysis.SelectionEnd = 30;
      Assert.AreEqual(10, analysis.CurrentIteration, "the range starts at its first iteration");
      for (int i = 0; i < 10; i++) analysis.NextIterationCommand.Execute(null);
      Assert.AreEqual(20, analysis.CurrentIteration);
      StringAssert.StartsWith(analysis.RangeText, "Iterations 10–30:");
      Assert.IsTrue(((SceneVisual)analysis.Visuals.Current!).Shapes.OfType<PathShape>().Count() >= 11, "iterations 10 to 19 under the current one");
      Shot(window, "range");
      window.Close();
      return 0;
    }, CancellationToken.None);
  }

  [TestMethod]
  [Timeout(300_000)]
  public async Task PopulationOnAFunctionLandscapeAndPackingsIn3D() {
    await session.Dispatch(async () => {
      var (window, detail) = await Open("GeneticAlgorithm", "SingleObjectiveTestFunctionProblem", null, "PopulationSize=40", "MaximumGenerations=30");
      await detail.Analysis.StartAsync();
      detail.Analysis.SelectionStart = 6;
      detail.Analysis.Trail = 6;
      Assert.AreEqual("Landscape", detail.Analysis.Visuals.Title);
      Shot(window, "landscape");
      window.Close();

      var (window3d, detail3d) = await Open("GeneticAlgorithm", "HeuristicLab.Problems.BinPacking3D.PermutationProblem", null, "PopulationSize=20", "MaximumGenerations=5");
      await detail3d.Analysis.StartAsync();
      detail3d.Analysis.SelectionStart = 3;
      Assert.IsInstanceOfType<BoxesVisual>(detail3d.Analysis.Visuals.Current, "3D pictures are shown without fading");
      Shot(window3d, "packing");
      window3d.Close();
      return 0;
    }, CancellationToken.None);
  }
}
