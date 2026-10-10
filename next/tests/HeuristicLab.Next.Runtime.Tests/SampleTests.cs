using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using HeuristicLab.Next.Runtime.Visuals;
using HeuristicLab.Optimization;
using HeuristicLab.Scripting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Next.Runtime.Tests;

/// <summary>The samples of HeuristicLab's start page load, run and can be drawn.</summary>
[TestClass]
public class SampleTests {
  [TestMethod]
  public async Task TheStartPageListsTheSameSamples() {
    var samples = await Samples.ListAsync();
    var byGroup = samples.GroupBy(s => s.Group).ToDictionary(g => g.Key, g => g.Select(s => s.Name).ToList());
    CollectionAssert.AreEqual(new[] {
      "ALPS Genetic Algorithm - TSP", "Evolution Strategy - Griewank", "Genetic Algorithm - Bin Packing Problem (3D)",
      "Genetic Algorithm - Graph Coloring", "Genetic Algorithm - TSP", "Genetic Algorithm - VRP", "Genetic Programming - Artificial Ant",
      "Genetic Programming - Multiplexer 11 Problem", "Genetic Programming - Robocode Java Source", "Grammatical Evolution - Artificial Ant (SantaFe)",
      "Island Genetic Algorithm - TSP", "Local Search - Knapsack", "Offspring Selection Evolution Strategy - Griewank",
      "Particle Swarm Optimization - Rastrigin", "RAPGA - Job Shop Scheduling", "Scatter Search - VRP", "Simulated Annealing - Rastrigin",
      "Tabu Search - TSP", "Tabu Search - VRP", "Variable Neighborhood Search - OP", "Variable Neighborhood Search - TSP"
    }, byGroup[Samples.StandardProblems]);
    CollectionAssert.AreEqual(new[] {
      "ALPS Genetic Programming - Symbolic Regression", "Gaussian Process Regression", "Genetic Programming - Shape-constrained Regression",
      "Genetic Programming - Structure Template", "Genetic Programming - Symbolic Classification", "Genetic Programming - Symbolic Regression",
      "Genetic Programming - Time Series Prediction (Mackey-Glass-17)", "Grammatical Evolution - Symbolic Regression (Poly-10)",
      "Offspring Selection Genetic Programming - Symbolic Regression"
    }, byGroup[Samples.DataAnalysis]);
    CollectionAssert.AreEqual(new[] {
      "Genetic Algorithm Script - QAP", "Grid Search Random Forest Script - Classification", "Grid Search Random Forest Script - Regression",
      "Grid Search SVM Script - Classification", "Grid Search SVM Script - Regression", "GUI Automation Script",
      "Offspring Selection Genetic Algorithm Script - Rastrigin"
    }, byGroup[Samples.Scripts]);
    Assert.AreEqual(2, samples.Count(s => s.Limitation != null), "Robocode and GUI automation");
  }

  [TestMethod]
  [Timeout(600_000)]
  public void EveryAlgorithmSampleRunsAndItsResultsCanBeDrawn() {
    var failures = new ConcurrentBag<string>();
    var ids = Samples.Ids.Where(id => Samples.GroupOf(id) != Samples.Scripts).ToList();
    Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = 4 }, id => {
      try {
        var optimizer = (IOptimizer)Samples.Load(id);
        var report = OptimizerRunner.RunAsync(optimizer, new RunOptions { Seed = 1, Timeout = TimeSpan.FromSeconds(id == "SGP_Robocode" ? 60 : 4) }).GetAwaiter().GetResult();
        if (id == "SGP_Robocode") {
          // documented limitation: without Robocode the evaluator reports what is missing
          if (report.Error?.Contains("Robocode") != true) failures.Add($"{id}: expected the missing Robocode error, got {report.Outcome} {report.Error}");
          return;
        }
        if (report.Outcome == RunOutcome.Failed) { failures.Add($"{id}: {report.Error?.Split('\n')[0]}"); return; }
        var algorithm = optimizer as IAlgorithm;
        if (algorithm == null || algorithm.Results.Count == 0) { failures.Add($"{id}: no results"); return; }
        foreach (var result in algorithm.Results) Visualizations.For(result.Value, result.Name);
      } catch (Exception e) {
        failures.Add($"{id}: {e.GetType().Name}: {e.Message}");
      }
    });
    Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures.Order()));
  }

  /// <summary>
  /// Every script compiles (except GUI automation, which needs the Windows GUI); the quick ones run.
  /// The SVM regression grid search runs too, but takes minutes (checked by hand).
  /// </summary>
  [TestMethod]
  [Timeout(600_000)]
  public void ScriptSamplesCompileAndRun() {
    var failures = new ConcurrentBag<string>();
    string[] run = ["GA_QAP_Script", "OSGA_Rastrigin_Script", "GridSearch_RF_Classification_Script", "GridSearch_RF_Regression_Script",
                    "GridSearch_SVM_Classification_Script"];
    var scripts = Samples.Ids.Where(id => Samples.GroupOf(id) == Samples.Scripts).ToList();
    Parallel.ForEach(scripts, new ParallelOptions { MaxDegreeOfParallelism = 4 }, id => {
      var script = (CSharpScript)Samples.Load(id);
      try {
        script.Compile();
        if (id == "GUI_Automation_Script") failures.Add($"{id}: compiled although it needs the Windows GUI");
      } catch (Exception) {
        var errors = script.CompileErrors.Cast<CompilerError>().Select(e => e.ErrorText).ToList();
        if (id != "GUI_Automation_Script" || !errors.Any(e => e.Contains("MainForm"))) failures.Add($"{id}: {string.Join("; ", errors.Take(3))}");
        return;
      }
      if (!run.Contains(id)) return;
      Exception? error = null;
      var output = new System.Text.StringBuilder();
      script.ScriptExecutionFinished += (_, e) => error = e.Value;
      script.ConsoleOutputChanged += (_, e) => { lock (output) output.Append(e.Value); };
      script.Execute();
      if (error != null) failures.Add($"{id}: {error.GetType().Name}: {error.Message}");
      else if (output.Length == 0 && !script.VariableStore.Keys.Any()) failures.Add($"{id}: neither output nor variables");
    });
    Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures.Order()));
  }
}
