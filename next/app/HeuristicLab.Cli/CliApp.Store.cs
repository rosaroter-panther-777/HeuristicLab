using System.CommandLine;
using System.Globalization;
using HeuristicLab.Next.Runtime;

namespace HeuristicLab.Cli;

public static partial class CliApp {
  private static Command StoreCommand(TextWriter output) {
    var command = new Command("store", "Inspect a results folder written by 'hl run --store'.");
    var directory = new Argument<DirectoryInfo>("folder") { Description = "Results folder" }.AcceptExistingOnly();
    var metric = new Option<string?>("--metric") { Description = "Result to show (default: BestQuality or CurrentBestQuality)" };

    var list = new Command("list", "One line per stored run.") { directory, metric };
    list.SetAction(result => {
      var rows = new ResultStore(result.GetValue(directory)!.FullName).Rows();
      var key = MetricColumn(rows, result.GetValue(metric));
      foreach (var row in rows)
        output.WriteLine($"{row["startedAt"][..19]}  {row["optimizer"],-30}  seed {row["seed"],-10}  {row["outcome"],-9}" +
                         (key == null ? "" : $"  {key[7..]}={Value(row, key)}"));
      output.WriteLine($"{rows.Count} runs");
      return ExitCompleted;
    });

    var by = new Option<string>("--by") { Description = "Column to group by, e.g. optimizer or param:PopulationSize", DefaultValueFactory = _ => "optimizer" };
    var summary = new Command("summary", "Statistics of a result per group of runs.") { directory, metric, by };
    summary.SetAction(result => {
      var rows = new ResultStore(result.GetValue(directory)!.FullName).Rows().Where(r => r["outcome"] == "Completed").ToList();
      var key = MetricColumn(rows, result.GetValue(metric));
      if (key == null) { output.WriteLine("No numeric result to summarize (use --metric)."); return ExitUsage; }
      var groupBy = result.GetValue(by)!;
      output.WriteLine($"{key[7..]} by {groupBy}:");
      output.WriteLine($"{"group",-36}  {"n",4}  {"mean",14}  {"sd",12}  {"min",14}  {"median",14}  {"max",14}");
      foreach (var group in rows.GroupBy(r => Value(r, groupBy)).OrderBy(g => g.Key, StringComparer.Ordinal)) {
        var stats = Statistics.Of(group.Select(r => Value(r, key)).Where(v => v != "").Select(v => double.Parse(v, CultureInfo.InvariantCulture)));
        if (stats == null) continue;
        output.WriteLine($"{Truncate(group.Key, 36),-36}  {stats.Count,4}  {G(stats.Mean),14}  {G(stats.StdDev),12}  {G(stats.Min),14}  {G(stats.Median),14}  {G(stats.Max),14}");
      }
      return ExitCompleted;
    });

    var csv = new Option<FileInfo?>("--csv") { Description = "Write the CSV to this file instead of the output" };
    var export = new Command("export", "All stored runs as CSV: one row per run, param:/result: columns.") { directory, csv };
    export.SetAction(result => {
      var rows = new ResultStore(result.GetValue(directory)!.FullName).Rows();
      if (result.GetValue(csv) is FileInfo file) {
        using var writer = new StreamWriter(file.FullName);
        ResultStore.WriteCsv(rows, writer);
        output.WriteLine($"Wrote {rows.Count} runs to {file.FullName}");
      } else {
        ResultStore.WriteCsv(rows, output);
      }
      return ExitCompleted;
    });

    command.Subcommands.Add(list);
    command.Subcommands.Add(summary);
    command.Subcommands.Add(export);
    return command;
  }

  private static string? MetricColumn(IReadOnlyList<IReadOnlyDictionary<string, string>> rows, string? metric) {
    if (metric != null) return "result:" + metric;
    return KeyMetrics.Select(k => "result:" + k).FirstOrDefault(k => rows.Any(r => r.ContainsKey(k)));
  }

  private static string Value(IReadOnlyDictionary<string, string> row, string column) =>
    row.TryGetValue(column, out var v) ? v : "";

  private static string Truncate(string text, int width) => text.Length <= width ? text : text[..(width - 1)] + "…";
}
