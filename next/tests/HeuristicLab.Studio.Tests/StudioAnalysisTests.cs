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
      Assert.AreEqual(41, analysis.Series[^1].Points.Count, "best so far for the initialization and 40 generations");
      Assert.IsTrue(analysis.TimeAxis.Count > 2, "elapsed time along the top");

      // click on iteration 20: its best solution in red, five earlier iterations in blue and (the run has
      // finished) five later ones in green, what changed since iteration 19 in yellow
      analysis.SelectionStart = 20;
      Assert.AreEqual(20, analysis.CurrentIteration);
      var scene = (SceneVisual)analysis.Visuals.Current!;
      var tours = scene.Shapes.OfType<PathShape>().ToList();
      Assert.AreEqual(Composition.Current, tours[^1].Color, "the current tour is red, on top");
      Assert.AreEqual(1, tours.Count(t => t.Color == Composition.Current));
      Assert.AreEqual(5, tours.Count(t => t.Color.B > t.Color.R && t.Color.B > t.Color.G), "earlier iterations: shades of blue");
      Assert.AreEqual(5, tours.Count(t => t.Color.G > t.Color.R && t.Color.G > t.Color.B), "later iterations: shades of green");
      Assert.IsTrue(tours.All(t => t.Color.A == 255), "no transparency");
      // yellow: the edges the crossover (and mutation) made that neither parent had
      int changedEdges = scene.Shapes.OfType<LineShape>().Count(l => l.Color == Composition.Changed);
      Assert.IsTrue(changedEdges is > 0 and < 65, $"{changedEdges} new edges: only what changed is yellow");
      CollectionAssert.IsSubsetOf(new[] { "Current", "Earlier iterations", "Later iterations" }, scene.Legend.Select(l => l.Label).ToList());
      StringAssert.Contains(analysis.StepText, "created #");
      Assert.IsTrue(analysis.HasSteps, "a GA creates many solutions per iteration");
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

      // a range: its start (wide, dark blue) and end (medium, dark green) under the current tour
      analysis.SelectionStart = 10;
      analysis.SelectionEnd = 30;
      Assert.AreEqual(10, analysis.CurrentIteration, "the range starts at its first iteration");
      for (int i = 0; i < 10; i++) analysis.NextIterationCommand.Execute(null);
      Assert.AreEqual(20, analysis.CurrentIteration);
      StringAssert.StartsWith(analysis.RangeText, "Iterations 10–30:");
      var ranged = ((SceneVisual)analysis.Visuals.Current!).Shapes.OfType<PathShape>().ToList();
      Assert.AreEqual(8.0, ranged.Single(p => p.Color == Composition.RangeStart).Thickness);
      Assert.AreEqual(4.5, ranged.Single(p => p.Color == Composition.RangeEnd).Thickness);
      Assert.IsTrue(ranged.FindIndex(p => p.Color == Composition.RangeStart) < ranged.FindIndex(p => p.Color == Composition.Current), "start under the current tour");
      Assert.AreEqual(9 + 9 + 3, ranged.Count, "iterations 11-19 and 21-29, start, end and the current one");
      Shot(window, "range");

      // play the range as an animation: iteration by iteration up to its end
      Assert.IsTrue(analysis.PlayCommand.CanExecute(null));
      analysis.Speed = 25;
      analysis.PlayCommand.Execute(null);
      Assert.IsTrue(analysis.IsPlaying);
      Assert.IsFalse(analysis.PlayCommand.CanExecute(null));
      for (int i = 0; i < 20 && analysis.IsPlaying; i++) analysis.AnimationTick();
      Assert.IsFalse(analysis.IsPlaying, "stops at the end of the range");
      Assert.AreEqual(30, analysis.CurrentIteration);
      analysis.SelectionEnd = null;
      Assert.IsFalse(analysis.PlayCommand.CanExecute(null), "playing needs a range");
      window.Close();
      return 0;
    }, CancellationToken.None);
  }

  [TestMethod]
  [Timeout(300_000)]
  public async Task TabuSearchMovesShowTheirNewEdges() {
    await session.Dispatch(async () => {
      var (window, detail) = await Open("TabuSearch", "TravelingSalesmanProblem", "TSPLIB (symmetric TSP)/ch130", "MaximumIterations=20");
      var analysis = detail.Analysis;
      await analysis.StartAsync();
      Assert.IsFalse(analysis.HasSteps, "one accepted move per iteration: step buttons are hidden");
      analysis.SelectionStart = 10;
      var scene = (SceneVisual)analysis.Visuals.Current!;
      // exactly the new edges are yellow: an inversion move replaces two edges, a translocation (the default) three
      int expected = analysis.StepText.Contains("Inversion") ? 2 : 3;
      StringAssert.Contains(analysis.StepText, "MoveMaker");
      Assert.AreEqual(expected, scene.Shapes.OfType<LineShape>().Count(l => l.Color == Composition.Changed), analysis.StepText);
      Shot(window, "tabu");
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
      Assert.IsInstanceOfType<BoxesVisual>(detail3d.Analysis.Visuals.Current, "3D pictures show the current solution only");
      Shot(window3d, "packing");
      window3d.Close();
      return 0;
    }, CancellationToken.None);
  }
}
