using System.CommandLine;
using HeuristicLab.Common;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Optimization;

namespace HeuristicLab.Cli;

public static partial class CliApp {
  private static Command InstancesCommand(TextWriter output) {
    var problem = new Argument<string>("problem") { Description = "Problem name or type (see: hl list problems)" };
    var command = new Command("instances", "List the benchmark instances a problem can load.") { problem };
    command.SetAction(result => {
      var entry = Catalog.Find(Catalog.Problems(), result.GetValue(problem)!);
      if (entry == null) { output.WriteLine($"Unknown problem '{result.GetValue(problem)}'."); return ExitUsage; }
      foreach (var instance in ProblemInstances.For((IProblem)entry.CreateInstance()))
        output.WriteLine(instance.QualifiedName);
      return ExitCompleted;
    });
    return command;
  }

  private static Command NewCommand(TextWriter output) {
    var algorithm = new Argument<string>("algorithm") { Description = "Algorithm name or type (see: hl list algorithms)" };
    var problem = new Option<string?>("--problem") { Description = "Problem name or type (default: the algorithm's own problem)" };
    var instance = new Option<string?>("--instance") { Description = "Benchmark instance to load (see: hl instances <problem>)" };
    var csv = new Option<FileInfo?>("--csv") { Description = "CSV file (header row) for a regression or classification problem" };
    var target = new Option<string?>("--target") { Description = "Target column of the CSV file" };
    var training = new Option<int>("--training") { Description = "Percent of CSV rows (from the top) used for training", DefaultValueFactory = _ => 66 };
    var set = new Option<string[]>("--set") { Description = "Set a parameter: Name=Value or Problem.Name=Value (repeatable)" };
    var name = new Option<string?>("--name") { Description = "Name of the new algorithm" };
    var outFile = new Option<FileInfo>("--out") { Description = "Where to save the .hl file", Required = true };
    var command = new Command("new", "Create an algorithm (with problem, instance or data) and save it as .hl.") {
      algorithm, problem, instance, csv, target, training, set, name, outFile
    };
    command.SetAction(result => {
      var algorithmEntry = Catalog.Find(Catalog.Algorithms(), result.GetValue(algorithm)!);
      if (algorithmEntry == null) { output.WriteLine($"Unknown algorithm '{result.GetValue(algorithm)}'. See: hl list algorithms"); return ExitUsage; }
      var created = (IAlgorithm)algorithmEntry.CreateInstance();

      if (result.GetValue(problem) is string problemName) {
        var problemEntry = Catalog.Find(Catalog.Problems(), problemName);
        if (problemEntry == null) { output.WriteLine($"Unknown problem '{problemName}'. See: hl list problems"); return ExitUsage; }
        var newProblem = (IProblem)problemEntry.CreateInstance();
        if (!created.ProblemType.IsInstanceOfType(newProblem)) {
          output.WriteLine($"{algorithmEntry.Name} cannot solve {problemEntry.Name} (needs {created.ProblemType.Name}).");
          return ExitUsage;
        }
        created.Problem = newProblem;
      }
      bool needsProblem = result.GetValue(instance) != null || result.GetValue(csv) != null;
      if (needsProblem && created.Problem == null) { output.WriteLine("--instance and --csv need a problem (--problem)."); return ExitUsage; }
      try {
        if (result.GetValue(instance) is string instanceName) {
          var loaded = ProblemInstances.Load(created.Problem!, instanceName);
          output.WriteLine($"Loaded instance {loaded.QualifiedName}");
        }
        if (result.GetValue(csv) is FileInfo csvFile) {
          if (result.GetValue(target) is not string targetColumn) { output.WriteLine("--csv needs --target."); return ExitUsage; }
          ProblemInstances.ImportCsv(created.Problem!, csvFile.FullName, targetColumn, result.GetValue(training));
          output.WriteLine($"Imported {csvFile.Name} (target {targetColumn}, {result.GetValue(training)} % training)");
        }
      } catch (ArgumentException e) {
        output.WriteLine(e.Message);
        return ExitUsage;
      }
      if (!TryApplySettings(created, result.GetValue(set), output)) return ExitUsage;
      if (result.GetValue(name) is string newName) created.Name = newName;

      var path = result.GetValue(outFile)!.FullName;
      Documents.Save((IStorableContent)created, path);
      output.WriteLine($"Saved {created.Name} to {path}");
      return ExitCompleted;
    });
    return command;
  }
}
