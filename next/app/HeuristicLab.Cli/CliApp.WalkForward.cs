using System.CommandLine;
using System.Globalization;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Optimization;

namespace HeuristicLab.Cli;

public static partial class CliApp {
  private static Command WalkForwardCommand(TextWriter output, TextWriter error) {
    var file = new Argument<FileInfo>("file") { Description = ".hl file with a data analysis algorithm (regression, classification, time series, trading)" }.AcceptExistingOnly();
    var train = new Option<int>("--train") { Description = "Rows per training window", Required = true };
    var test = new Option<int>("--test") { Description = "Rows per test window (directly after its training window)", Required = true };
    var step = new Option<int?>("--step") { Description = "Rows the windows move per fold (default: test size)" };
    var expanding = new Option<bool>("--expanding") { Description = "Training always starts at --start and grows (default: rolling window)" };
    var start = new Option<int>("--start") { Description = "First training row (e.g. after a warm-up)" };
    var seed = new Option<int?>("--seed") { Description = "Seed for every fold" };
    var parallel = new Option<int>("--parallel") { Description = "Folds run at the same time", DefaultValueFactory = _ => 1 };
    var store = new Option<DirectoryInfo?>("--store") { Description = "Add every fold's run report to this results folder" };
    var report = new Option<FileInfo?>("--out") { Description = "Write the walk-forward report as JSON" };
    var quiet = new Option<bool>("--quiet") { Description = "No per-fold output" };
    var command = new Command("walkforward", "Walk-forward validation: train and test on successive time windows.") {
      file, train, test, step, expanding, start, seed, parallel, store, report, quiet
    };
    command.SetAction(async (result, cancellationToken) => {
      var path = result.GetValue(file)!.FullName;
      if (Documents.Load(path) is not IAlgorithm algorithm) { error.WriteLine("Walk-forward validation needs an algorithm."); return ExitUsage; }
      var options = new WalkForwardOptions(result.GetValue(train), result.GetValue(test)) {
        Step = result.GetValue(step), Expanding = result.GetValue(expanding), Start = result.GetValue(start)
      };
      var resultStore = result.GetValue(store) is DirectoryInfo dir ? new ResultStore(dir.FullName) : null;
      WalkForwardReport walk;
      try {
        walk = await WalkForward.RunAsync(algorithm, options, result.GetValue(seed), result.GetValue(parallel), null, path, fold => {
          lock (output) {
            resultStore?.Add(fold.Report);
            if (!result.GetValue(quiet))
              output.WriteLine($"fold {fold.Index}: train {fold.Training.Start}..{fold.Training.End - 1}, test {fold.Test.Start}..{fold.Test.End - 1}: " +
                               $"{fold.Report.Outcome}{TestMetrics(fold.Report)}");
          }
        }, cancellationToken);
      } catch (ArgumentException e) {
        error.WriteLine(e.Message);
        return ExitUsage;
      }
      output.WriteLine($"{walk.Folds.Count} folds. Test results over folds:");
      if (walk.TestSummary.Count > 0) {
        int width = walk.TestSummary.Keys.Max(k => k.Length);
        output.WriteLine($"{"Result".PadRight(width)}  {"n",4}  {"mean",14}  {"sd",12}  {"min",14}  {"median",14}  {"max",14}");
        foreach (var (name, s) in walk.TestSummary)
          output.WriteLine($"{name.PadRight(width)}  {s.Count,4}  {G(s.Mean),14}  {G(s.StdDev),12}  {G(s.Min),14}  {G(s.Median),14}  {G(s.Max),14}");
      }
      if (result.GetValue(report) is FileInfo reportFile) await WriteJsonAsync(reportFile, walk, output);
      if (resultStore != null) output.WriteLine($"Stored in: {resultStore.Directory}");
      if (cancellationToken.IsCancellationRequested) return ExitStopped;
      return walk.Folds.Any(f => f.Report.Outcome == RunOutcome.Failed) ? ExitFailed : ExitCompleted;
    });
    return command;
  }

  // the few test metrics most worth a glance per fold
  private static string TestMetrics(RunReport report) {
    var results = report.Runs.LastOrDefault()?.Results;
    if (results == null) return "";
    var picks = results.Where(r => r.Key.Contains("(test)") && (r.Key.Contains("R²") || r.Key.Contains("Sharpe") || r.Key.Contains("Profit")
                                                                || r.Key.Contains("Root mean squared error") || r.Key.Contains("Accuracy")))
      .Where(r => r.Value.Value is double).Take(3)
      .Select(r => $"{r.Key[(r.Key.LastIndexOf('.') + 1)..]}={((double)r.Value.Value!).ToString("G4", CultureInfo.InvariantCulture)}");
    var text = string.Join(", ", picks);
    return text.Length == 0 ? "" : ", " + text;
  }
}
