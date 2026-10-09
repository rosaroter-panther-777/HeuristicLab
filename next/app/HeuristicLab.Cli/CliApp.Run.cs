using System.CommandLine;
using System.Globalization;
using HeuristicLab.Common;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Optimization;

namespace HeuristicLab.Cli;

public static partial class CliApp {
  private static Command RunCommand(TextWriter output, TextWriter error) {
    var file = new Argument<FileInfo>("file") { Description = ".hl file with an algorithm, experiment or batch run" }.AcceptExistingOnly();
    var seed = new Option<int?>("--seed") { Description = "Seed (with --repeat: seed of the first run, then +1 per run)" };
    var set = new Option<string[]>("--set") { Description = "Set a parameter before running: Name=Value (repeatable)", AllowMultipleArgumentsPerToken = false };
    var sweep = new Option<string[]>("--sweep") { Description = "Sweep a parameter: Name=v1,v2,... (repeatable: full factorial; same seeds per configuration)" };
    var repeat = new Option<int>("--repeat") { Description = "Number of seeded runs (statistics over all of them)", DefaultValueFactory = _ => 1 };
    var parallel = new Option<int>("--parallel") { Description = "Runs executed at the same time with --repeat", DefaultValueFactory = _ => 1 };
    var timeout = new Option<TimeSpan?>("--timeout") { Description = "Stop each run after this wall-clock time (hh:mm:ss)" };
    var report = new Option<FileInfo?>("--out") { Description = "Write the run (or batch) report as JSON to this file" };
    var store = new Option<DirectoryInfo?>("--store") { Description = "Add every run report to this results folder" };
    var save = new Option<FileInfo?>("--save") { Description = "Save the optimizer with its runs to this .hl file (single runs only)" };
    var quiet = new Option<bool>("--quiet") { Description = "No progress output" };
    var command = new Command("run", "Run a .hl file and report its results.") {
      file, seed, set, sweep, repeat, parallel, timeout, report, store, save, quiet
    };
    command.SetAction(async (result, cancellationToken) => {
      var path = result.GetValue(file)!.FullName;
      if (Documents.Load(path) is not IOptimizer optimizer) {
        error.WriteLine($"{path} does not contain an algorithm, experiment or batch run.");
        return ExitUsage;
      }
      int repetitions = result.GetValue(repeat), parallelism = result.GetValue(parallel);
      if (repetitions < 1 || parallelism < 1) { error.WriteLine("--repeat and --parallel must be at least 1."); return ExitUsage; }
      var sweeps = result.GetValue(sweep) ?? [];
      if ((repetitions > 1 || sweeps.Length > 0) && result.GetValue(save) != null) { error.WriteLine("--save works for single runs only."); return ExitUsage; }
      var settings = result.GetValue(set);
      if (settings is { Length: > 0 }) {
        if (optimizer is not Core.IParameterizedItem parameterized) {
          error.WriteLine("--set needs an algorithm; experiments and batch runs have no parameters of their own.");
          return ExitUsage;
        }
        if (!TryApplySettings(parameterized, settings, error)) return ExitUsage;
      }

      var options = new RunOptions { Timeout = result.GetValue(timeout) };
      var resultStore = result.GetValue(store) is DirectoryInfo dir ? new ResultStore(dir.FullName) : null;
      bool showProgress = !result.GetValue(quiet);

      if (sweeps.Length > 0) {
        IReadOnlyList<SweepResult> sweepResults;
        try {
          sweepResults = await Sweeps.RunAsync(optimizer, sweeps, repetitions, result.GetValue(seed), parallelism, options, path,
            (config, i, r) => {
              resultStore?.Add(r);
              if (showProgress) lock (error) error.WriteLine($"[{config}] run {i + 1}/{repetitions} seed {r.RequestedSeed}: {r.Outcome}{KeyMetric(r)}");
            }, cancellationToken);
        } catch (ArgumentException e) {
          error.WriteLine(e.Message);
          return ExitUsage;
        }
        WriteSweepSummary(output, sweepResults);
        if (result.GetValue(report) is FileInfo sweepFile) await WriteJsonAsync(sweepFile, sweepResults, output);
        if (resultStore != null) output.WriteLine($"Stored in: {resultStore.Directory}");
        return sweepResults.SelectMany(r => r.Batch.Reports).Any(r => r.Outcome == RunOutcome.Failed) ? ExitFailed
             : cancellationToken.IsCancellationRequested ? ExitStopped : ExitCompleted;
      }

      if (repetitions == 1) {
        var progress = showProgress ? new Progress<RunProgress>(p => error.WriteLine(FormatProgress(p))) : null;
        var runReport = await OptimizerRunner.RunAsync(optimizer, options with { Seed = result.GetValue(seed) }, path, progress, cancellationToken);
        WriteSummary(output, runReport);
        resultStore?.Add(runReport);
        if (result.GetValue(report) is FileInfo reportFile) await WriteJsonAsync(reportFile, runReport, output);
        if (result.GetValue(save) is FileInfo saveFile) {
          Documents.Save((IStorableContent)optimizer, saveFile.FullName);
          output.WriteLine($"Saved: {saveFile.FullName}");
        }
        if (resultStore != null) output.WriteLine($"Stored in: {resultStore.Directory}");
        if (runReport.Error != null) error.WriteLine(runReport.Error);
        return ExitCode(runReport.Outcome);
      }

      var batch = await BatchRunner.RepeatAsync(BatchRunner.Copies(optimizer), repetitions, result.GetValue(seed), parallelism,
        options, path, (i, r) => {
          resultStore?.Add(r);
          if (showProgress) lock (error) error.WriteLine($"run {i + 1}/{repetitions} seed {r.RequestedSeed}: {r.Outcome}{KeyMetric(r)}");
        }, cancellationToken);
      WriteBatchSummary(output, batch);
      if (result.GetValue(report) is FileInfo batchFile) await WriteJsonAsync(batchFile, batch, output);
      if (resultStore != null) output.WriteLine($"Stored in: {resultStore.Directory}");
      if (cancellationToken.IsCancellationRequested || batch.Reports.Count < repetitions) return ExitStopped;
      return batch.Reports.Any(r => r.Outcome == RunOutcome.Failed) ? ExitFailed
           : batch.Reports.Any(r => r.Outcome == RunOutcome.Stopped) ? ExitStopped : ExitCompleted;
    });
    return command;
  }

  internal static bool TryApplySettings(Core.IParameterizedItem item, string[]? assignments, TextWriter error) {
    if (assignments == null || assignments.Length == 0) return true;
    try {
      ParameterEditor.Apply(item, assignments);
      return true;
    } catch (ArgumentException e) {
      error.WriteLine(e.Message);
      return false;
    }
  }

  private static int ExitCode(RunOutcome outcome) => outcome switch {
    RunOutcome.Completed => ExitCompleted, RunOutcome.Stopped => ExitStopped, _ => ExitFailed
  };

  private static async Task WriteJsonAsync<T>(FileInfo file, T value, TextWriter output) {
    await File.WriteAllTextAsync(file.FullName, ReportJson.Serialize(value), CancellationToken.None);
    output.WriteLine($"Report: {file.FullName}");
  }

  // the result most people look at first, if the algorithm has one
  private static readonly string[] KeyMetrics = ["BestQuality", "CurrentBestQuality"];

  private static string KeyMetric(RunReport report) {
    var results = report.Runs.LastOrDefault()?.Results;
    var key = KeyMetrics.FirstOrDefault(k => results?.ContainsKey(k) == true);
    return key == null ? "" : $", {key}={Convert.ToString(results![key].Value, CultureInfo.InvariantCulture)}";
  }

  private static void WriteBatchSummary(TextWriter output, BatchReport batch) {
    output.WriteLine($"{batch.Completed} of {batch.Repetitions} runs completed, seeds {batch.BaseSeed}..{batch.BaseSeed + batch.Repetitions - 1}");
    if (batch.Summary.Count == 0) return;
    int width = Math.Max(6, batch.Summary.Keys.Max(k => k.Length));
    output.WriteLine($"{"Result".PadRight(width)}  {"n",4}  {"mean",14}  {"sd",12}  {"min",14}  {"median",14}  {"max",14}");
    foreach (var (name, s) in batch.Summary)
      output.WriteLine($"{name.PadRight(width)}  {s.Count,4}  {G(s.Mean),14}  {G(s.StdDev),12}  {G(s.Min),14}  {G(s.Median),14}  {G(s.Max),14}");
  }

  private static void WriteSweepSummary(TextWriter output, IReadOnlyList<SweepResult> results) {
    var metric = KeyMetrics.FirstOrDefault(k => results.Any(r => r.Batch.Summary.ContainsKey(k)));
    if (metric == null) { output.WriteLine($"{results.Count} configurations run."); return; }
    int width = Math.Max(13, results.Max(r => r.Configuration.Length));
    output.WriteLine($"{metric} by configuration (seeds {results[0].Batch.BaseSeed}..{results[0].Batch.BaseSeed + results[0].Batch.Repetitions - 1} for each):");
    output.WriteLine($"{"configuration".PadRight(width)}  {"n",4}  {"mean",14}  {"sd",12}  {"min",14}  {"median",14}  {"max",14}");
    foreach (var r in results)
      if (r.Batch.Summary.TryGetValue(metric, out var s))
        output.WriteLine($"{r.Configuration.PadRight(width)}  {s.Count,4}  {G(s.Mean),14}  {G(s.StdDev),12}  {G(s.Min),14}  {G(s.Median),14}  {G(s.Max),14}");
  }

  private static string G(double value) => value.ToString("G8", CultureInfo.InvariantCulture);
}
