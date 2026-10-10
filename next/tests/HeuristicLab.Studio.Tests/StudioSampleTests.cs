using Avalonia.Headless;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Next.Runtime.Visuals;
using HeuristicLab.Optimization;
using HeuristicLab.Studio.ViewModels;
using HeuristicLab.Studio.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Studio.Tests;

/// <summary>The start page samples in Studio: listed, opened as experiments or scripts, and run.</summary>
[TestClass]
public class StudioSampleTests {
  private static HeadlessUnitTestSession session = null!;

  [ClassInitialize]
  public static void Start(TestContext _) => session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

  [TestMethod]
  [Timeout(300_000)]
  public async Task SamplesOpenAsExperimentsAndRun() {
    await session.Dispatch(async () => {
      var vm = new MainViewModel(null, null);
      var ws = vm.Workspace;
      var window = new MainWindow { DataContext = vm, Width = 1440, Height = 880 };
      window.Show();
      Assert.AreSame(ws.Samples, ws.Detail, "with nothing selected the start page shows the samples");
      await ws.Samples.LoadAsync();
      Assert.AreEqual(3, ws.Samples.Groups.Count);
      Assert.AreEqual(37, ws.Samples.Groups.Sum(g => g.Samples.Count));
      Avalonia.Threading.Dispatcher.UIThread.RunJobs();
      window.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "studio-samples.png"));

      // the ant sample (it failed before the analyzer fix) becomes an experiment and runs
      await ws.Samples.OpenAsync("SGP_SantaFe");
      var experiment = ws.Experiments.Single();
      Assert.AreEqual("Genetic Programming - Artificial Ant", experiment.Children.Single().Name);
      var algorithm = (AlgorithmBlockViewModel)experiment.Children.Single();
      Assert.IsTrue(experiment.IsReady, experiment.Missing);
      ParameterEditor.Apply(algorithm.Algorithm, ["MaximumGenerations=5"]);
      await ws.StartAllCommand.ExecuteAsync(null);
      Assert.AreEqual(1, experiment.Experiment.Runs.Count, ws.Status);
      ws.Selected = algorithm;
      var detail = (AlgorithmDetailViewModel)ws.Detail!;
      detail.Refresh();
      Assert.AreEqual(1, detail.Runs.Rows.Count);
      detail.SelectedTab = AlgorithmDetailViewModel.ResultsTab;
      detail.Results.Selected = detail.Results.Entries.Single(e => e.Name == "Best Solution");
      Assert.AreEqual("Ant trail", detail.Results.Detail!.Visuals.Title);
      Avalonia.Threading.Dispatcher.UIThread.RunJobs();
      window.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "studio-sample-santafe.png"));
      return 0;
    }, CancellationToken.None);
  }

  [TestMethod]
  [Timeout(300_000)]
  public async Task ScriptSampleRunsInTheScriptsTab() {
    await session.Dispatch(async () => {
      var vm = new MainViewModel(null, null);
      var window = new MainWindow { DataContext = vm, Width = 1440, Height = 880 };
      window.Show();
      await vm.Workspace.Samples.OpenAsync("GA_QAP_Script");
      Assert.AreEqual(MainViewModel.ScriptsTab, vm.SelectedTab);
      var script = vm.Scripts.Selected!;
      Assert.AreEqual("Genetic Algorithm Script - QAP", script.Name);
      await script.RunAsync();
      Assert.AreEqual(0, script.Errors.Count, string.Join("; ", script.Errors));
      StringAssert.StartsWith(script.Status, "Finished");
      var chart = script.Variables.Single(v => v.Name == "qualityChart");
      script.SelectedTab = ScriptViewModel.VariablesTab;
      script.SelectedVariable = chart;
      Assert.IsInstanceOfType<ChartPanel>(script.VariableDetail!.Visuals.Current, "the script's chart is drawn like a result");
      Avalonia.Threading.Dispatcher.UIThread.RunJobs();
      window.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "studio-script-qap.png"));

      // the GUI automation script needs the Windows GUI: its compile errors are shown
      await vm.Workspace.Samples.OpenAsync("GUI_Automation_Script");
      var gui = vm.Scripts.Selected!;
      await gui.RunAsync();
      Assert.IsTrue(gui.Errors.Any(e => e.Text.Contains("MainForm")));
      Assert.AreEqual(ScriptViewModel.ErrorsTab, gui.SelectedTab);
      Avalonia.Threading.Dispatcher.UIThread.RunJobs();
      window.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "studio-script-errors.png"));
      return 0;
    }, CancellationToken.None);
  }
}
