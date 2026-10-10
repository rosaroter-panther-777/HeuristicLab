using System.Globalization;
using HeuristicLab.Analysis;
using HeuristicLab.Optimization;

namespace HeuristicLab.Next.Runtime.Runs;

/// <param name="Key">"source", "origin", "run", "param:X", "result:Y", "label:Z" or a results folder's own keys.</param>
/// <param name="Kind">Run, Parameter, Result or Label.</param>
public sealed record RunColumn(string Key, string Name, string Kind, bool IsNumeric) {
  public override string ToString() => Kind is "Parameter" or "Result" ? $"{Name} ({Kind.ToLowerInvariant()})" : Name;
}

/// <summary>One run: its values by column key (numbers as double, everything else as text), and the run itself if it is in memory.</summary>
public sealed class RunRow(string source, IReadOnlyDictionary<string, object> values, IRun? run) {
  public string Source { get; } = source;
  public IRun? Run { get; } = run;
  public IReadOnlyDictionary<string, object> Values { get; } = values;

  public double? Number(string key) => Values.TryGetValue(key, out var v) && v is double d ? d : null;

  public string Text(string key) => Values.TryGetValue(key, out var v)
    ? v is double d ? d.ToString("G10", CultureInfo.InvariantCulture) : (string)v
    : "";
}

/// <summary>An in-memory run and where it comes from: the chosen source and its origin (experiment / batch run / algorithm).</summary>
public sealed record SourcedRun(string Source, string Origin, IRun Run);

/// <summary>
/// Runs as a table, like HeuristicLab's run collection table: one row per run, one column per
/// parameter and scalar result (plus where the run comes from). Runs come from optimizers in memory
/// (experiments, batch runs, time-limit runs, algorithms) or from results folders.
/// </summary>
public sealed class RunTable {
  private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

  public RunTable(IReadOnlyList<RunRow> rows) {
    Rows = rows;
    var keys = rows.SelectMany(r => r.Values.Keys).Distinct().ToList();
    Columns = keys.Select(k => new RunColumn(k, NameOf(k), KindOf(k), rows.All(r => !r.Values.TryGetValue(k, out var v) || v is double)))
      .OrderBy(c => c.Kind switch { "Run" => 0, "Label" => 1, "Parameter" => 2, _ => 3 })
      .ThenBy(c => c.Kind == "Run" ? Array.IndexOf(RunKeys, c.Key) : 0)
      .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
  }

  private static readonly string[] RunKeys = ["source", "origin", "run", "optimizer", "outcome", "seed", "startedAt"];

  public IReadOnlyList<RunRow> Rows { get; }
  public IReadOnlyList<RunColumn> Columns { get; }

  public RunColumn? Column(string key) => Columns.FirstOrDefault(c => c.Key == key);

  private static string KindOf(string key) =>
    key.StartsWith("param:", StringComparison.Ordinal) ? "Parameter"
    : key.StartsWith("result:", StringComparison.Ordinal) ? "Result"
    : key.StartsWith("label:", StringComparison.Ordinal) ? "Label" : "Run";

  private static string NameOf(string key) => key.IndexOf(':') is int i and > 0 ? key[(i + 1)..] : key switch {
    "source" => "Source", "origin" => "Origin", "run" => "Run", "optimizer" => "Optimizer", "outcome" => "Outcome",
    "seed" => "Seed", "startedAt" => "Started", _ => key
  };

  /// <summary>Runs of optimizers in memory; the same run reached from two sources is listed once.</summary>
  public static RunTable FromRuns(IEnumerable<SourcedRun> runs) {
    var seen = new HashSet<IRun>(ReferenceEqualityComparer.Instance);
    var rows = new List<RunRow>();
    foreach (var (source, origin, run) in runs) {
      if (!seen.Add(run)) continue;
      var values = new Dictionary<string, object> { ["source"] = source, ["origin"] = origin, ["run"] = run.Name };
      var record = OptimizerRunner.ToRecord(run);
      foreach (var (name, value) in record.Parameters) if (Cell(value) is { } cell) values["param:" + name] = cell;
      foreach (var (name, value) in record.Results) if (Cell(value) is { } cell) values["result:" + name] = cell;
      rows.Add(new RunRow(source, values, run));
    }
    return new RunTable(rows);
  }

  /// <summary>Scalars as numbers, text and choices (operators by name) as text; arrays and tables are not cells.</summary>
  private static object? Cell(ItemValue value) => value.Value switch {
    double d => d,
    int i => (double)i,
    long l => (double)l,
    float f => (double)f,
    bool b => b ? "True" : "False",
    string s => s,
    null => value.Text,
    _ => null
  };

  /// <summary>A results folder ("hl run --store", Studio's results folder): its rows as they are, numbers where they parse.</summary>
  public static RunTable FromStore(string source, IEnumerable<IReadOnlyDictionary<string, string>> rows) =>
    new(rows.Select(r => {
      var values = new Dictionary<string, object> { ["source"] = source };
      foreach (var (key, text) in r) {
        if (text.Length == 0) continue;
        values[key] = (key.StartsWith("result:", StringComparison.Ordinal) || key.StartsWith("param:", StringComparison.Ordinal) || key == "seed")
                      && double.TryParse(text, NumberStyles.Float, Invariant, out var d) ? d : text;
      }
      if (!values.ContainsKey("run") && r.TryGetValue("optimizer", out var optimizer)) values["run"] = optimizer;
      return new RunRow(source, values, null);
    }).ToList());

  public static RunTable Combine(IEnumerable<RunTable> tables) => new(tables.SelectMany(t => t.Rows).ToList());

  public RunTable Where(IEnumerable<RunFilter> filters) {
    var list = filters.ToList();
    return list.Count == 0 ? this : new RunTable(Rows.Where(r => list.All(f => f.Matches(r))).ToList());
  }

  /// <summary>Names of the tables (DataTable results, e.g. "Qualities") the in-memory runs have, with their rows.</summary>
  public IReadOnlyList<(string Table, IReadOnlyList<string> Rows)> Curves() =>
    Rows.Where(r => r.Run != null).SelectMany(r => r.Run!.Results.Where(x => x.Value is DataTable).Select(x => (x.Key, (DataTable)x.Value)))
      .GroupBy(t => t.Key).Select(g => (g.Key, (IReadOnlyList<string>)g.SelectMany(t => t.Item2.Rows.Select(row => row.Name)).Distinct().ToList()))
      .OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase).ToList();

  public void WriteCsv(TextWriter writer, IEnumerable<string>? keys = null) {
    var columns = keys?.ToList() ?? Columns.Select(c => c.Key).ToList();
    static string Quote(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    writer.WriteLine(string.Join(",", columns.Select(k => Quote(Column(k)?.ToString() ?? k))));
    foreach (var row in Rows)
      writer.WriteLine(string.Join(",", columns.Select(k => Quote(row.Number(k) is double d ? d.ToString("R", Invariant) : row.Text(k)))));
  }
}

/// <param name="Op">=, ≠, &lt;, ≤, &gt;, ≥ or contains; numbers compare as numbers.</param>
public sealed record RunFilter(string Key, string Op, string Value) {
  public static readonly IReadOnlyList<string> Operators = ["=", "≠", "<", "≤", ">", "≥", "contains"];

  public bool Matches(RunRow row) {
    if (row.Number(Key) is double x && double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
      return Op switch { "=" => x == y, "≠" => x != y, "<" => x < y, "≤" => x <= y, ">" => x > y, "≥" => x >= y, _ => row.Text(Key).Contains(Value, StringComparison.OrdinalIgnoreCase) };
    string text = row.Text(Key);
    int order = string.Compare(text, Value, StringComparison.OrdinalIgnoreCase);
    return Op switch {
      "=" => order == 0, "≠" => order != 0, "<" => order < 0, "≤" => order <= 0, ">" => order > 0, "≥" => order >= 0,
      _ => text.Contains(Value, StringComparison.OrdinalIgnoreCase)
    };
  }

  public override string ToString() => $"{Key} {Op} {Value}";
}

public sealed record RunGroup(string Name, IReadOnlyList<RunRow> Rows);

/// <summary>
/// Groups by any combination of columns ("Algorithm Name" and "PopulationSize", ...): every
/// combination of values is a group, named by its values. Numeric columns with more distinct
/// values than bins are put into equal-width intervals; no columns: one group of all runs.
/// </summary>
public static class RunGrouping {
  public static IReadOnlyList<RunGroup> Group(RunTable table, IReadOnlyList<string> keys, int bins = 5) {
    if (keys.Count == 0) return table.Rows.Count == 0 ? [] : [new RunGroup("All runs", table.Rows)];
    var labelers = keys.Select(k => Labeler(table, k, bins)).ToList();
    return table.Rows.GroupBy(r => string.Join(" · ", labelers.Select(l => l(r))))
      .OrderBy(g => g.Key, NaturalComparer.Instance)
      .Select(g => new RunGroup(g.Key, g.ToList())).ToList();
  }

  private static Func<RunRow, string> Labeler(RunTable table, string key, int bins) {
    var column = table.Column(key);
    string name = column?.Name ?? key;
    if (column is { IsNumeric: true }) {
      var values = table.Rows.Select(r => r.Number(key)).OfType<double>().ToList();
      if (values.Count > 0 && values.Distinct().Count() > Math.Max(1, bins)) {
        double min = values.Min(), max = values.Max(), width = (max - min) / bins;
        return r => r.Number(key) is double v
          ? $"{name} {Bin(Math.Min(bins - 1, (int)((v - min) / width)), min, width)}"
          : $"{name} –";
      }
      return r => r.Number(key) is double v ? $"{name} {v.ToString("G6", CultureInfo.InvariantCulture)}" : $"{name} –";
    }
    // text columns: the value says enough (an algorithm's or problem's name); labels and parameters get their name
    bool named = column?.Kind is "Parameter" or "Label";
    return r => r.Text(key) is { Length: > 0 } t ? (named ? $"{name} {t}" : t) : $"{name} –";
  }

  private static string Bin(int index, double min, double width) =>
    $"[{(min + index * width).ToString("G4", CultureInfo.InvariantCulture)}, {(min + (index + 1) * width).ToString("G4", CultureInfo.InvariantCulture)})";
}

/// <summary>Orders "Batch 2" before "Batch 10".</summary>
internal sealed class NaturalComparer : IComparer<string> {
  public static readonly NaturalComparer Instance = new();
  public int Compare(string? a, string? b) {
    a ??= ""; b ??= "";
    int i = 0, j = 0;
    while (i < a.Length && j < b.Length) {
      if (char.IsDigit(a[i]) && char.IsDigit(b[j])) {
        int si = i, sj = j;
        while (i < a.Length && char.IsDigit(a[i])) i++;
        while (j < b.Length && char.IsDigit(b[j])) j++;
        int c = decimal.Parse(a[si..i], CultureInfo.InvariantCulture).CompareTo(decimal.Parse(b[sj..j], CultureInfo.InvariantCulture));
        if (c != 0) return c;
      } else {
        int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
        if (c != 0) return c;
        i++; j++;
      }
    }
    return (a.Length - i).CompareTo(b.Length - j);
  }
}
