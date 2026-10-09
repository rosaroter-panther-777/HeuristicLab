using System.CommandLine;
using System.Globalization;
using HeuristicLab.Next.Runtime;

namespace HeuristicLab.Cli;

public static partial class CliApp {
  private static Command DataCommand(TextWriter output) {
    var command = new Command("data", "Inspect data files and derive features (CSV or Parquet).");
    var file = new Argument<FileInfo>("file") { Description = "CSV or Parquet file" }.AcceptExistingOnly();

    var info = new Command("info", "Columns, types, missing values and basic statistics.") { file };
    info.SetAction(result => {
      var table = DataFiles.Read(result.GetValue(file)!.FullName);
      output.WriteLine($"{table.Rows} rows, {table.Names.Count} columns");
      int width = Math.Max(6, table.Names.Max(n => n.Length));
      output.WriteLine($"{"column".PadRight(width)}  {"type",-8}  {"missing",7}  {"min",12}  {"max",12}  {"mean",12}  {"sd",12}");
      foreach (var c in ColumnSummary.Of(table)) {
        var range = c.Type == "number"
          ? $"{N(c.Min),12}  {N(c.Max),12}  {N(c.Mean),12}  {N(c.StdDev),12}"
          : $"{c.First,-12}  {c.Last,-12}";
        output.WriteLine($"{c.Name.PadRight(width)}  {c.Type,-8}  {c.Missing,7}  {range}");
      }
      return ExitCompleted;
    });

    var add = new Option<string[]>("--add") {
      Description = "Feature to add (repeatable): return(x) logreturn(x) diff(x) lag(x,k|a..b) lead(x,k) mean|std|min|max|zscore(x,w)",
      Required = true
    };
    var dropna = new Option<bool>("--dropna") { Description = "Drop rows where a derived feature is missing (warm-up, lead tail)" };
    var outFile = new Option<FileInfo>("--out") { Description = "Output file (.csv or .parquet)", Required = true };
    var derive = new Command("derive", "Add derived features (in row order) and write a new data file.") { file, add, dropna, outFile };
    derive.SetAction(result => {
      var table = DataFiles.Read(result.GetValue(file)!.FullName);
      TabularData derived;
      try {
        derived = Features.Derive(table, result.GetValue(add)!, result.GetValue(dropna));
      } catch (ArgumentException e) {
        output.WriteLine(e.Message);
        return ExitUsage;
      }
      var path = result.GetValue(outFile)!.FullName;
      DataFiles.Write(derived, path);
      output.WriteLine($"Wrote {derived.Rows} rows, {derived.Names.Count} columns ({derived.Names.Count - table.Names.Count} new) to {path}");
      return ExitCompleted;
    });

    command.Subcommands.Add(info);
    command.Subcommands.Add(derive);
    return command;
  }

  private static string N(double? value) => value?.ToString("G6", CultureInfo.InvariantCulture) ?? "";
}
