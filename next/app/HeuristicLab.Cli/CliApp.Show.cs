using System.CommandLine;
using System.Globalization;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Optimization;

namespace HeuristicLab.Cli;

public static partial class CliApp {
  private static Command ShowCommand(TextWriter output) {
    var file = new Argument<FileInfo>("file") { Description = ".hl file of an optimizer that has run (e.g. saved with hl run --save)" }.AcceptExistingOnly();
    var solution = new Option<string?>("--solution") { Description = "Show only the solution with this result name" };
    var predictions = new Option<FileInfo?>("--predictions") { Description = "Write per-row actual/predicted values (or price change/signal) as CSV" };
    var json = new Option<bool>("--json") { Description = "Write the solutions as JSON (including per-row series)" };
    var command = new Command("show", "Show what an optimizer found: models, formulas, metrics, tours.") { file, solution, predictions, json };
    command.SetAction(result => {
      if (Documents.Load(result.GetValue(file)!.FullName) is not IOptimizer optimizer) {
        output.WriteLine("The file does not contain an algorithm, experiment or batch run.");
        return ExitUsage;
      }
      var views = Solutions.Find(optimizer).ToList();
      if (result.GetValue(solution) is string name) views = views.Where(v => v.Name == name).ToList();
      if (views.Count == 0) {
        output.WriteLine("No solutions found. Has the optimizer run? (hl run --save stores the results)");
        return ExitUsage;
      }
      if (result.GetValue(json)) {
        output.WriteLine(ReportJson.Serialize(views));
      } else {
        foreach (var view in views) {
          output.WriteLine($"{view.Name} [{view.Kind}] {view.Title}");
          if (view.Model != null) output.WriteLine($"  model: {view.Model}");
          if (view.Training.End > 0) output.WriteLine($"  rows: training {view.Training.Start}..{view.Training.End - 1}, test {view.Test.Start}..{view.Test.End - 1}");
          foreach (var (metric, value) in view.Metrics.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
            output.WriteLine($"  {metric} = {value.ToString("G8", CultureInfo.InvariantCulture)}");
        }
      }
      if (result.GetValue(predictions) is FileInfo csv) {
        var view = views.FirstOrDefault(v => v.Actual != null);
        if (view == null) { output.WriteLine("No solution with per-row predictions."); return ExitUsage; }
        using var writer = new StreamWriter(csv.FullName);
        Solutions.WritePredictionsCsv(view, writer);
        output.WriteLine($"Wrote predictions of {view.Name} to {csv.FullName}");
      }
      return ExitCompleted;
    });
    return command;
  }
}
