using System.Globalization;
using System.Text;

namespace HeuristicLab.Next.Runtime;

/// <summary>
/// A folder of run reports, one immutable JSON file per optimizer run. Simple on purpose:
/// files can be versioned, copied, inspected and read from any tool; Rows() flattens them into
/// one row per recorded run for comparison and CSV export (pandas, R, spreadsheets).
/// </summary>
public sealed class ResultStore(string directory) {
  public string Directory { get; } = Path.GetFullPath(directory);

  public string Add(RunReport report) {
    System.IO.Directory.CreateDirectory(Directory);
    var seed = report.RequestedSeed is int s ? $"-seed{s}" : "";
    var name = $"{report.Provenance.StartedAt:yyyyMMdd'T'HHmmss'Z'}-{Slug(report.Optimizer)}{seed}-{Guid.NewGuid().ToString("N")[..8]}.json";
    var path = Path.Combine(Directory, name);
    File.WriteAllText(path, ReportJson.Serialize(report));
    return path;
  }

  public IReadOnlyList<(string File, RunReport Report)> Reports() =>
    !System.IO.Directory.Exists(Directory) ? []
      : System.IO.Directory.EnumerateFiles(Directory, "*.json").Order(StringComparer.Ordinal)
        .Select(f => (Path.GetFileName(f), ReportJson.Deserialize<StoredReport>(File.ReadAllText(f))!.ToRunReport()))
        .ToList();

  /// <summary>
  /// One row per recorded run: report columns, then "param:Name" and "result:Name" columns for
  /// every scalar parameter/result (tables and other structured values are left out).
  /// </summary>
  public IReadOnlyList<IReadOnlyDictionary<string, string>> Rows() {
    var rows = new List<IReadOnlyDictionary<string, string>>();
    foreach (var (file, report) in Reports()) {
      foreach (var run in report.Runs) {
        var row = new Dictionary<string, string>(StringComparer.Ordinal) {
          ["file"] = file,
          ["startedAt"] = report.Provenance.StartedAt.ToString("O", CultureInfo.InvariantCulture),
          ["optimizer"] = report.Optimizer,
          ["optimizerType"] = report.OptimizerType,
          ["run"] = run.Name,
          ["outcome"] = report.Outcome.ToString(),
          ["seed"] = report.RequestedSeed?.ToString(CultureInfo.InvariantCulture) ?? "",
          ["executionSeconds"] = report.ExecutionSeconds.ToString("R", CultureInfo.InvariantCulture),
          ["inputFile"] = report.Provenance.InputFile ?? "",
          ["inputSha256"] = report.Provenance.InputSha256 ?? "",
          ["tool"] = $"{report.Provenance.Tool} {report.Provenance.ToolVersion}",
          ["runtime"] = report.Provenance.Runtime,
          ["os"] = report.Provenance.OperatingSystem,
        };
        foreach (var (name, value) in run.Parameters) if (Scalar(value) is string v) row["param:" + name] = v;
        foreach (var (name, value) in run.Results) if (Scalar(value) is string v) row["result:" + name] = v;
        rows.Add(row);
      }
    }
    return rows;
  }

  public static void WriteCsv(IReadOnlyList<IReadOnlyDictionary<string, string>> rows, TextWriter writer) {
    var fixedColumns = rows.FirstOrDefault()?.Keys.Where(k => !k.Contains(':')).ToList() ?? [];
    var columns = fixedColumns
      .Concat(rows.SelectMany(r => r.Keys).Where(k => k.StartsWith("param:")).Distinct().Order(StringComparer.Ordinal))
      .Concat(rows.SelectMany(r => r.Keys).Where(k => k.StartsWith("result:")).Distinct().Order(StringComparer.Ordinal))
      .ToList();
    writer.WriteLine(string.Join(",", columns.Select(Quote)));
    foreach (var row in rows)
      writer.WriteLine(string.Join(",", columns.Select(c => Quote(row.TryGetValue(c, out var v) ? v : ""))));
  }

  private static string? Scalar(ItemValue value) => value.Value switch {
    double d => d.ToString("R", CultureInfo.InvariantCulture),
    float f => f.ToString("R", CultureInfo.InvariantCulture),
    int or long or bool => Convert.ToString(value.Value, CultureInfo.InvariantCulture),
    string s => s,
    System.Text.Json.JsonElement e => e.ValueKind switch {
      System.Text.Json.JsonValueKind.Number => e.GetRawText(),
      System.Text.Json.JsonValueKind.String => e.GetString(),
      System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => e.GetRawText(),
      _ => null
    },
    null => value.Text,
    _ => null
  };

  private static string Quote(string value) =>
    value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

  private static string Slug(string text) {
    var sb = new StringBuilder();
    foreach (var c in text.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : '-');
    var slug = string.Join("-", sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
    return slug.Length > 40 ? slug[..40] : slug;
  }

  // JSON shape for reading reports back (record positional constructors need matching names)
  private sealed record StoredReport(
    Provenance Provenance, string Optimizer, string OptimizerType, int? RequestedSeed, RunOutcome Outcome,
    string? Error, double ExecutionSeconds, List<RunRecord> Runs) {
    public RunReport ToRunReport() => new(Provenance, Optimizer, OptimizerType, RequestedSeed, Outcome, Error, ExecutionSeconds, Runs);
  }
}
