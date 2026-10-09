using System.CommandLine;
using System.Globalization;
using HeuristicLab.Common;
using HeuristicLab.Core;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Optimization;

namespace HeuristicLab.Cli;

/// <summary>The hl command line. Output goes to the given writers so it can run in-process (tests).</summary>
public static class CliApp {
  public const int ExitCompleted = 0, ExitFailed = 1, ExitUsage = 2, ExitStopped = 3;

  public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error) {
    var root = new RootCommand("HeuristicLab command line: inspect and run .hl files.") {
      ListCommand(output), InfoCommand(output), RunCommand(output, error)
    };
    var config = new InvocationConfiguration { Output = output, Error = error };
    return await root.Parse(args).InvokeAsync(config);
  }

  private static Command ListCommand(TextWriter output) {
    var kind = new Argument<string>("kind") { Description = "algorithms or problems" };
    kind.AcceptOnlyFromAmong("algorithms", "problems");
    var json = new Option<bool>("--json") { Description = "Write JSON instead of a table" };
    var command = new Command("list", "List the algorithms or problems that can be created.") { kind, json };
    command.SetAction(result => {
      var entries = result.GetValue(kind) == "algorithms" ? Catalog.Algorithms() : Catalog.Problems();
      if (result.GetValue(json)) {
        output.WriteLine(ReportJson.Serialize(entries.Select(e => new { e.Name, e.TypeName, e.Description })));
      } else {
        int width = entries.Max(e => e.Name.Length);
        foreach (var e in entries) output.WriteLine($"{e.Name.PadRight(width)}  {e.TypeName}");
      }
      return ExitCompleted;
    });
    return command;
  }

  private static Command InfoCommand(TextWriter output) {
    var file = new Argument<FileInfo>("file") { Description = ".hl file" }.AcceptExistingOnly();
    var json = new Option<bool>("--json") { Description = "Write JSON instead of text" };
    var command = new Command("info", "Show what a .hl file contains.") { file, json };
    command.SetAction(result => {
      var content = Documents.Load(result.GetValue(file)!.FullName);
      var info = Describe(content);
      if (result.GetValue(json)) {
        output.WriteLine(ReportJson.Serialize(info));
      } else {
        output.WriteLine($"{info.Name} ({info.Type})");
        if (info.Problem != null) output.WriteLine($"Problem: {info.Problem}");
        if (info.State != null) output.WriteLine($"State: {info.State}, stored runs: {info.StoredRuns}");
        if (info.Parameters.Count > 0) {
          output.WriteLine("Parameters:");
          int width = info.Parameters.Keys.Max(k => k.Length);
          foreach (var (name, value) in info.Parameters) output.WriteLine($"  {name.PadRight(width)}  {value}");
        }
      }
      return ExitCompleted;
    });
    return command;
  }

  private static Command RunCommand(TextWriter output, TextWriter error) {
    var file = new Argument<FileInfo>("file") { Description = ".hl file with an algorithm, experiment or batch run" }.AcceptExistingOnly();
    var seed = new Option<int?>("--seed") { Description = "Seed for all algorithms (sets SetSeedRandomly=false)" };
    var timeout = new Option<TimeSpan?>("--timeout") { Description = "Stop after this wall-clock time (hh:mm:ss)" };
    var report = new Option<FileInfo?>("--out") { Description = "Write the run report as JSON to this file" };
    var save = new Option<FileInfo?>("--save") { Description = "Save the optimizer with its runs to this .hl file" };
    var quiet = new Option<bool>("--quiet") { Description = "No progress output" };
    var command = new Command("run", "Run a .hl file and report its results.") { file, seed, timeout, report, save, quiet };
    command.SetAction(async (result, cancellationToken) => {
      var path = result.GetValue(file)!.FullName;
      if (Documents.Load(path) is not IOptimizer optimizer) {
        error.WriteLine($"{path} does not contain an algorithm, experiment or batch run.");
        return ExitUsage;
      }
      var progress = result.GetValue(quiet) ? null : new Progress<RunProgress>(p => error.WriteLine(FormatProgress(p)));
      var options = new RunOptions { Seed = result.GetValue(seed), Timeout = result.GetValue(timeout) };
      var runReport = await OptimizerRunner.RunAsync(optimizer, options, path, progress, cancellationToken);

      WriteSummary(output, runReport);
      if (result.GetValue(report) is FileInfo reportFile) {
        await File.WriteAllTextAsync(reportFile.FullName, ReportJson.Serialize(runReport), CancellationToken.None);
        output.WriteLine($"Report: {reportFile.FullName}");
      }
      if (result.GetValue(save) is FileInfo saveFile) {
        Documents.Save((IStorableContent)optimizer, saveFile.FullName);
        output.WriteLine($"Saved: {saveFile.FullName}");
      }
      if (runReport.Error != null) error.WriteLine(runReport.Error);
      return runReport.Outcome switch {
        RunOutcome.Completed => ExitCompleted, RunOutcome.Stopped => ExitStopped, _ => ExitFailed
      };
    });
    return command;
  }

  public sealed record ContentInfo(
    string Name, string Type, string? Problem, string? State, int? StoredRuns, IReadOnlyDictionary<string, string> Parameters);

  public static ContentInfo Describe(IStorableContent content) {
    var name = content is INamedItem named ? named.Name : content.GetType().Name;
    var parameters = content is IParameterizedItem p
      ? p.Parameters.Where(x => !x.Hidden).ToDictionary(x => x.Name, x => x.ActualValue?.ToString() ?? "")
      : new Dictionary<string, string>();
    var problem = content is IAlgorithm { Problem: not null } a ? $"{a.Problem.Name} ({a.Problem.GetType().FullName})" : null;
    var optimizer = content as IOptimizer;
    return new ContentInfo(name, content.GetType().FullName!, problem, optimizer?.ExecutionState.ToString(),
      optimizer?.Runs.Count, parameters);
  }

  private static readonly string[] ProgressKeys =
    { "Generations", "Iterations", "EvaluatedSolutions", "BestQuality", "CurrentBestQuality", "BestKnownQuality" };

  private static string FormatProgress(RunProgress p) {
    var values = ProgressKeys.Where(p.Values.ContainsKey)
      .Select(k => $"{k}={p.Values[k].ToString("G6", CultureInfo.InvariantCulture)}");
    return $"[{p.ExecutionTime:hh\\:mm\\:ss}] {string.Join(" ", values)}".TrimEnd();
  }

  private static void WriteSummary(TextWriter output, RunReport report) {
    output.WriteLine($"{report.Optimizer}: {report.Outcome} after {TimeSpan.FromSeconds(report.ExecutionSeconds):hh\\:mm\\:ss\\.fff}" +
                     (report.RequestedSeed is int s ? $" (seed {s})" : ""));
    foreach (var run in report.Runs) {
      output.WriteLine($"{run.Name}:");
      foreach (var (name, value) in run.Results.Where(r => r.Value.Value is double or int or long or bool))
        output.WriteLine($"  {name} = {Convert.ToString(value.Value, CultureInfo.InvariantCulture)}");
    }
  }
}
