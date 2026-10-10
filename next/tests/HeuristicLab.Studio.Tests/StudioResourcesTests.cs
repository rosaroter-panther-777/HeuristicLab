using System.Diagnostics;
using Avalonia.Headless;
using HeuristicLab.Data;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Optimization;
using HeuristicLab.Studio.Services;
using HeuristicLab.Studio.ViewModels;
using HeuristicLab.Studio.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Studio.Tests;

[TestClass]
[DoNotParallelize]
public class StudioResourcesTests {
  private static HeadlessUnitTestSession session = null!;

  [ClassInitialize]
  public static void Start(TestContext _) => session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

  [TestMethod]
  [Timeout(300_000)]
  public async Task LimitsApplyAndRunsRecordThem() {
    var settingsFile = Path.Combine(Path.GetTempPath(), $"studio-settings-{Guid.NewGuid():N}.json");
    try {
      await session.Dispatch(async () => {
        var vm = new MainViewModel(null, null, new JsonSettingsStore(settingsFile));
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 960 };
        window.Show();
        var resources = vm.Resources;
        resources.Cores = 2;
        resources.ThreadsPerAlgorithm = 2;
        resources.ConcurrentExperiments = 1;
        StringAssert.StartsWith(resources.Applied, "Now: 2 of");
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
          Assert.AreEqual((nint)3, Process.GetCurrentProcess().ProcessorAffinity, "two cores for the whole program");

        // two experiments, one at a time; their runs carry the limits
        var ws = vm.Workspace;
        foreach (var name in new[] { "A", "B" }) {
          var experiment = ws.NewExperiment();
          var ga = ws.AddAlgorithm(experiment, Catalog.Find(Catalog.Algorithms(), "GeneticAlgorithm")!);
          ws.SetProblem(ga, (IProblem)Setups.Create(new SetupRequest("GeneticAlgorithm") { Problem = "TravelingSalesmanProblem", Instance = "ch130" }).Algorithm.Problem!.Clone());
          ParameterEditor.Apply(ga.Algorithm, ["MaximumGenerations=10"]);
          experiment.IsChecked = true;
        }
        await ws.StartAllCommand.ExecuteAsync(null);
        var runs = ws.Experiments.SelectMany(e => e.Experiment.Runs).ToList();
        Assert.AreEqual(2, runs.Count);
        Assert.IsTrue(runs.All(r => ((IntValue)r.Parameters[HeuristicLab.Next.Runtime.Resources.CoresParameter]).Value == 2));
        Assert.IsTrue(runs.All(r => ((IntValue)r.Parameters[HeuristicLab.Next.Runtime.Resources.ThreadsParameter]).Value == 2));
        var algorithm = (EngineAlgorithm)ExperimentTree.Algorithms(ws.Experiments[0].Experiment).Single();
        Assert.AreEqual(2, ((HeuristicLab.ParallelEngine.ParallelEngine)algorithm.Engine).DegreeOfParallelism, "two threads: the parallel engine");

        // the Results tab can group by them
        vm.SelectedTab = MainViewModel.ResultsTab;
        foreach (var source in vm.RunAnalysis.Sources) source.IsChecked = true;
        vm.RunAnalysis.GroupToAdd = vm.RunAnalysis.AllColumns.Single(c => c.Key == "param:" + HeuristicLab.Next.Runtime.Resources.CoresParameter);
        Assert.AreEqual("Resources: CPU cores 2", vm.RunAnalysis.Groups.Single().Name);

        vm.SelectedTab = MainViewModel.ResourcesTab;
        await Task.Delay(1500);
        resources.RefreshUsage();
        StringAssert.StartsWith(resources.Usage, "CPU ");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "studio-resources.png"));
        window.Close();

        // remembered: a new session starts with the same limits
        var again = new MainViewModel(null, null, new JsonSettingsStore(settingsFile));
        Assert.AreEqual(2, (int)again.Resources.Cores);
        Assert.AreEqual(1, (int)again.Resources.ConcurrentExperiments);
        again.Resources.DefaultsCommand.Execute(null);
        Assert.AreEqual(Environment.ProcessorCount, (int)again.Resources.Cores);
        Assert.AreEqual(1, (int)again.Resources.ThreadsPerAlgorithm);
        return 0;
      }, CancellationToken.None);
    } finally {
      HeuristicLab.Next.Runtime.Resources.Apply(new ResourceLimits());
      File.Delete(settingsFile);
    }
  }
}
