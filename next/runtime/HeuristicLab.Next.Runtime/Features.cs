using System.Collections;
using System.Globalization;

namespace HeuristicLab.Next.Runtime;

/// <summary>
/// Derived columns for time-ordered (e.g. financial) data, as composable expressions over
/// columns, evaluated row by row in table order:
///   return(x)  x[t]/x[t-1]-1          logreturn(x)  ln(x[t]/x[t-1])     diff(x)  x[t]-x[t-1]
///   lag(x,k)   x[t-k]                 lead(x,k)     x[t+k] (targets only - looks ahead!)
///   mean(x,w) std(x,w) min(x,w) max(x,w)  rolling over the last w rows including t
///   zscore(x,w)  (x[t]-mean)/std over the last w rows
/// x is a column name or another expression; lag(x,1..5) expands to five columns. Rows without
/// enough history (or future, for lead) get NaN.
/// </summary>
public static class Features {
  public static TabularData Derive(TabularData table, IEnumerable<string> expressions, bool dropIncomplete = false) {
    var cache = new Dictionary<string, List<double>>(StringComparer.Ordinal);
    var added = new List<(string, IList)>();
    foreach (var expression in expressions.SelectMany(Expand)) {
      var name = Normalize(expression);
      if (table.Names.Contains(name) || added.Any(a => a.Item1 == name)) continue;
      added.Add((name, Evaluate(table, name, cache)));
    }
    var result = table.WithColumns(added);
    return dropIncomplete ? result.WhereFinite(added.Select(a => a.Item1)) : result;
  }

  /// <summary>Splits a comma-separated list of column names/expressions at top-level commas only.</summary>
  public static IReadOnlyList<string> SplitList(string list) {
    var items = new List<string>();
    int depth = 0, start = 0;
    for (int i = 0; i <= list.Length; i++) {
      if (i == list.Length || (list[i] == ',' && depth == 0)) {
        var item = list[start..i].Trim();
        if (item.Length > 0) items.Add(item);
        start = i + 1;
      } else if (list[i] == '(') depth++;
      else if (list[i] == ')') depth--;
    }
    return items;
  }

  /// <summary>"lag(x,1..3)" -> "lag(x,1)", "lag(x,2)", "lag(x,3)"; other expressions unchanged.</summary>
  public static IEnumerable<string> Expand(string expression) {
    var (function, argument, number) = Parse(expression.Trim());
    if (function == null || number == null || !number.Contains("..")) return [expression.Trim()];
    var parts = number.Split("..");
    int from = int.Parse(parts[0], CultureInfo.InvariantCulture), to = int.Parse(parts[1], CultureInfo.InvariantCulture);
    if (to < from) throw new ArgumentException($"Empty range in '{expression}'.");
    return Enumerable.Range(from, to - from + 1).Select(k => $"{function}({argument},{k})");
  }

  private static string Normalize(string expression) {
    var (function, argument, number) = Parse(expression.Trim());
    if (function == null) return expression.Trim();
    var inner = Normalize(argument!);
    return number == null ? $"{function}({inner})" : $"{function}({inner},{number})";
  }

  private static List<double> Evaluate(TabularData table, string expression, Dictionary<string, List<double>> cache) {
    if (cache.TryGetValue(expression, out var cached)) return cached;
    if (table.IndexOf(expression) >= 0) return table.Numbers(expression);
    var (function, argument, number) = Parse(expression);
    if (function == null) throw new ArgumentException($"Unknown column or expression '{expression}'. Columns: {string.Join(", ", table.Names)}");
    var x = Evaluate(table, argument!, cache);
    int Window(int min) {
      if (number == null || !int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) || w < min)
        throw new ArgumentException($"{function} needs a whole number >= {min} as second argument: '{expression}'.");
      return w;
    }
    int n = x.Count;
    var y = new List<double>(n);
    switch (function) {
      case "return": for (int t = 0; t < n; t++) y.Add(t == 0 ? double.NaN : x[t] / x[t - 1] - 1); break;
      case "logreturn": for (int t = 0; t < n; t++) y.Add(t == 0 ? double.NaN : Math.Log(x[t] / x[t - 1])); break;
      case "diff": for (int t = 0; t < n; t++) y.Add(t == 0 ? double.NaN : x[t] - x[t - 1]); break;
      case "lag": { int k = Window(1); for (int t = 0; t < n; t++) y.Add(t - k >= 0 ? x[t - k] : double.NaN); break; }
      case "lead": { int k = Window(1); for (int t = 0; t < n; t++) y.Add(t + k < n ? x[t + k] : double.NaN); break; }
      case "mean" or "std" or "min" or "max" or "zscore": {
        int w = Window(function is "std" or "zscore" ? 2 : 1);
        for (int t = 0; t < n; t++) {
          if (t + 1 < w) { y.Add(double.NaN); continue; }
          var window = x.GetRange(t + 1 - w, w);
          double mean = window.Average();
          double sd = Math.Sqrt(window.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, w - 1));
          y.Add(function switch {
            "mean" => mean, "std" => sd, "min" => window.Min(), "max" => window.Max(),
            _ => sd > 0 ? (x[t] - mean) / sd : double.NaN
          });
        }
        break;
      }
      default: throw new ArgumentException($"Unknown function '{function}' in '{expression}'.");
    }
    cache[expression] = y;
    return y;
  }

  /// <summary>Splits "f(arg,number)" at the top-level comma; (null, null, null) if not a call.</summary>
  private static (string? Function, string? Argument, string? Number) Parse(string expression) {
    int open = expression.IndexOf('(');
    if (open <= 0 || !expression.EndsWith(')')) return (null, null, null);
    var function = expression[..open].Trim().ToLowerInvariant();
    if (!function.All(char.IsLetter)) return (null, null, null);
    var inner = expression[(open + 1)..^1];
    int depth = 0, comma = -1;
    for (int i = 0; i < inner.Length; i++) {
      if (inner[i] == '(') depth++;
      else if (inner[i] == ')') depth--;
      else if (inner[i] == ',' && depth == 0) comma = i;
    }
    return comma < 0 ? (function, inner.Trim(), null) : (function, inner[..comma].Trim(), inner[(comma + 1)..].Trim());
  }
}

/// <summary>Per-column overview of a data table.</summary>
public sealed record ColumnSummary(string Name, string Type, int Count, int Missing,
  double? Min, double? Max, double? Mean, double? StdDev, string? First, string? Last) {
  public static IReadOnlyList<ColumnSummary> Of(TabularData table) =>
    table.Names.Select((name, i) => table.Columns[i] switch {
      List<double> d => Numeric(name, d),
      List<DateTime> t => new ColumnSummary(name, "datetime", t.Count, 0, null, null, null, null,
        t.Count > 0 ? Date(t[0]) : null, t.Count > 0 ? Date(t[^1]) : null),
      List<string> s => new ColumnSummary(name, "text", s.Count, s.Count(string.IsNullOrEmpty), null, null, null, null,
        s.FirstOrDefault(), s.LastOrDefault()),
      _ => throw new InvalidOperationException()
    }).ToList();

  private static string Date(DateTime value) =>
    value.ToString(value.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

  private static ColumnSummary Numeric(string name, List<double> values) {
    var finite = values.Where(double.IsFinite).ToList();
    var stats = Statistics.Of(finite);
    return new ColumnSummary(name, "number", values.Count, values.Count - finite.Count,
      stats?.Min, stats?.Max, stats?.Mean, stats?.StdDev, null, null);
  }
}
