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
    var data = new Option<FileInfo?>("--data", "--csv") { Description = "CSV or Parquet file for regression, classification, time series or trading problems" };
    var target = new Option<string?>("--target") { Description = "Target column of the data file" };
    var inputs = new Option<string?>("--inputs") { Description = "Comma-separated input columns, ranges allowed: lag(x,1..5) (default: all numeric, non-constant columns)" };
    var training = new Option<int>("--training") { Description = "Percent of rows (from the top) used for training", DefaultValueFactory = _ => 66 };
    var trainingStart = new Option<int>("--training-start") { Description = "Rows at the top not used for training (warm-up, e.g. AR time offset)" };
    var set = new Option<string[]>("--set") { Description = "Set a parameter: Name=Value or Problem.Name=Value (repeatable)" };
    var name = new Option<string?>("--name") { Description = "Name of the new algorithm" };
    var outFile = new Option<FileInfo>("--out") { Description = "Where to save the .hl file", Required = true };
    var command = new Command("new", "Create an algorithm (with problem, instance or data) and save it as .hl.") {
      algorithm, problem, instance, data, target, inputs, training, trainingStart, set, name, outFile
    };
    command.SetAction(result => {
      var request = new SetupRequest(result.GetValue(algorithm)!) {
        Problem = result.GetValue(problem), Instance = result.GetValue(instance),
        DataFile = result.GetValue(data)?.FullName, Target = result.GetValue(target),
        Inputs = result.GetValue(inputs) is string list ? Features.SplitList(list).SelectMany(Features.Expand).ToList() : null,
        TrainingPercent = result.GetValue(training), TrainingStart = result.GetValue(trainingStart), Settings = result.GetValue(set) ?? [], Name = result.GetValue(name)
      };
      SetupResult setup;
      try {
        setup = Setups.Create(request);
      } catch (ArgumentException e) {
        output.WriteLine(e.Message);
        return ExitUsage;
      }
      foreach (var message in setup.Messages) output.WriteLine(message);
      var path = result.GetValue(outFile)!.FullName;
      Documents.Save((IStorableContent)setup.Algorithm, path);
      output.WriteLine($"Saved {setup.Algorithm.Name} to {path}");
      return ExitCompleted;
    });
    return command;
  }
}
