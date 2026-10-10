using System.Diagnostics;
using HeuristicLab.Data;
using HeuristicLab.Optimization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Next.Runtime.Tests;

[TestClass]
[DoNotParallelize]  // changes the whole test process (affinity, priority)
public class ResourceTests {
  [TestCleanup]
  public void Restore() => Resources.Apply(new ResourceLimits());

  private static IAlgorithm Ga(int seed) => Setups.Create(new SetupRequest("GeneticAlgorithm") {
    Problem = "TravelingSalesmanProblem", Instance = "ch130", Settings = ["PopulationSize=60", "MaximumGenerations=40"]
  }).Algorithm;

  [TestMethod]
  public void CoresLimitTheProcess() {
    if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) Assert.Inconclusive("affinity: Linux and Windows");
    var problems = Resources.Apply(new ResourceLimits { Cores = 2 });
    Assert.AreEqual(0, problems.Count, string.Join("; ", problems));
    Assert.AreEqual((nint)3, Process.GetCurrentProcess().ProcessorAffinity);
    Resources.Apply(new ResourceLimits());
    Assert.AreEqual(Environment.ProcessorCount, System.Numerics.BitOperations.PopCount((ulong)(long)Process.GetCurrentProcess().ProcessorAffinity));
  }

  [TestMethod]
  public void ThreadsPerAlgorithmChooseTheEngineAndTheSeedDecidesTheOutcome() {
    double Best(int threads) {
      Resources.Apply(new ResourceLimits { ThreadsPerAlgorithm = threads });
      var ga = Ga(1);
      Resources.Configure(ga);
      var engine = ((EngineAlgorithm)ga).Engine;
      if (threads == 1) Assert.IsInstanceOfType<HeuristicLab.SequentialEngine.SequentialEngine>(engine);
      else Assert.AreEqual(threads, ((HeuristicLab.ParallelEngine.ParallelEngine)engine).DegreeOfParallelism);
      var report = OptimizerRunner.RunAsync(ga, new RunOptions { Seed = 5 }).GetAwaiter().GetResult();
      Assert.IsNull(report.Error);
      Resources.Tag(ga.Runs);
      var run = ga.Runs.Single();
      Assert.AreEqual(threads, ((IntValue)run.Parameters[Resources.ThreadsParameter]).Value, "the run records its resources");
      return ((DoubleValue)run.Results["BestQuality"]).Value;
    }
    // HeuristicLab evaluates in parallel but varies (crossover, mutation) sequentially: more threads, same result
    Assert.AreEqual(Best(1), Best(4));
  }

  [TestMethod]
  public void TheMonitorSamplesTheProcess() {
    using var monitor = new ResourceMonitor(TimeSpan.FromMilliseconds(50));
    var sample = monitor.Sample();
    Assert.IsTrue(sample.WorkingSetMB > 10 && sample.Threads > 0 && sample.CpuPercent is >= 0 and <= 100);
    Assert.IsTrue(Resources.TotalMemoryGB > 0.1);
    StringAssert.StartsWith(Resources.Gpu, "Not used");
  }
}
