using System.Collections;
using System.Globalization;
using HeuristicLab.Problems.Instances.DataAnalysis;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace HeuristicLab.Next.Runtime;

/// <summary>
/// A data table as HeuristicLab datasets use it: named columns, each a List&lt;double&gt;,
/// List&lt;string&gt; or List&lt;DateTime&gt; of the same length. Missing numbers are NaN.
/// </summary>
public sealed class TabularData {
  public TabularData(IEnumerable<string> names, IEnumerable<IList> columns) {
    Names = names.ToList();
    Columns = columns.ToList();
    if (Names.Count != Columns.Count) throw new ArgumentException("One name per column expected.");
    if (Names.Distinct(StringComparer.Ordinal).Count() != Names.Count) throw new ArgumentException("Column names must be unique.");
    Rows = Columns.Count == 0 ? 0 : Columns[0].Count;
    if (Columns.Any(c => c.Count != Rows)) throw new ArgumentException("All columns must have the same length.");
    foreach (var c in Columns)
      if (c is not (List<double> or List<string> or List<DateTime>))
        throw new ArgumentException($"Unsupported column type {c.GetType().Name}.");
  }

  public IReadOnlyList<string> Names { get; }
  public IReadOnlyList<IList> Columns { get; }
  public int Rows { get; }

  public IList Column(string name) {
    int i = IndexOf(name);
    return i >= 0 ? Columns[i] : throw new ArgumentException($"No column '{name}'. Columns: {string.Join(", ", Names)}");
  }
  public List<double> Numbers(string name) =>
    Column(name) as List<double> ?? throw new ArgumentException($"Column '{name}' is not numeric.");
  public int IndexOf(string name) => Names.ToList().IndexOf(name);
  public IEnumerable<string> NumericNames => Names.Where((_, i) => Columns[i] is List<double>);

  public TabularData WithColumns(IEnumerable<(string Name, IList Values)> added) {
    var extra = added.ToList();
    return new TabularData(Names.Concat(extra.Select(a => a.Name)), Columns.Concat(extra.Select(a => a.Values)));
  }

  /// <summary>Rows with all given numeric columns finite (e.g. to drop the warm-up of lags/rolling windows).</summary>
  public TabularData WhereFinite(IEnumerable<string> numericColumns) {
    var check = numericColumns.Select(Numbers).ToList();
    var keep = Enumerable.Range(0, Rows).Where(r => check.All(c => double.IsFinite(c[r]))).ToList();
    return new TabularData(Names, Columns.Select(c => Select(c, keep)));
  }

  private static IList Select(IList column, List<int> rows) => column switch {
    List<double> d => rows.Select(r => d[r]).ToList(),
    List<string> s => rows.Select(r => s[r]).ToList(),
    List<DateTime> t => rows.Select(r => t[r]).ToList(),
    _ => throw new InvalidOperationException()
  };
}

/// <summary>Reading and writing data tables: CSV (format detected by HeuristicLab's parser) and Parquet.</summary>
public static class DataFiles {
  // The Parquet library is async-only. Blocking on it from a thread with a synchronization context
  // (a UI thread) deadlocks if any await inside resumes on that context, so the async work runs on
  // the thread pool, where there is no context to capture.
  public static TabularData Read(string path) =>
    IsParquet(path) ? Task.Run(() => ReadParquetAsync(path)).GetAwaiter().GetResult() : ReadCsv(path);

  public static void Write(TabularData table, string path) {
    if (IsParquet(path)) Task.Run(() => WriteParquetAsync(table, path)).GetAwaiter().GetResult();
    else WriteCsv(table, path);
  }

  private static bool IsParquet(string path) => path.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase);

  private static TabularData ReadCsv(string path) {
    TableFileParser.DetermineFileFormat(path, out var numberFormat, out var dateFormat, out var separator);
    // HeuristicLab's guess can pick the wrong separator (e.g. with ISO timestamps); the header line
    // decides: take the candidate that splits it into the most fields
    var headerLine = File.ReadLines(path).FirstOrDefault() ?? "";
    var best = new[] { ',', ';', '\t', '|' }.MaxBy(c => headerLine.Split(c).Length);
    if (headerLine.Split(best).Length > headerLine.Split(separator).Length) {
      separator = best;
      if (best == ';' && numberFormat.NumberDecimalSeparator == ".") numberFormat = new CultureInfo("de-DE").NumberFormat;
      if (best == ',' && numberFormat.NumberDecimalSeparator == ",") numberFormat = CultureInfo.InvariantCulture.NumberFormat;
    }
    var parser = new TableFileParser();
    bool header = parser.AreColumnNamesInFirstLine(path, numberFormat, dateFormat, separator);
    parser.Parse(path, numberFormat, dateFormat, separator, header);
    return new TabularData(parser.VariableNames, parser.Values);
  }

  private static void WriteCsv(TabularData table, string path) {
    using var writer = new StreamWriter(path);
    writer.WriteLine(string.Join(",", table.Names.Select(Quote)));
    for (int r = 0; r < table.Rows; r++)
      writer.WriteLine(string.Join(",", table.Columns.Select(c => Quote(c switch {
        List<double> d => double.IsNaN(d[r]) ? "" : d[r].ToString("R", CultureInfo.InvariantCulture),
        List<DateTime> t => t[r].ToString(t[r].TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        _ => Convert.ToString(c[r], CultureInfo.InvariantCulture) ?? ""
      }))));
  }

  private static string Quote(string value) =>
    value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

  private static async Task<TabularData> ReadParquetAsync(string path) {
    using var stream = File.OpenRead(path);
    using var reader = await ParquetReader.CreateAsync(stream).ConfigureAwait(false);
    var fields = reader.Schema.GetDataFields();
    var columns = fields.Select(f => NewColumn(f.ClrType)).ToList();
    for (int g = 0; g < reader.RowGroupCount; g++) {
      using var group = reader.OpenRowGroupReader(g);
      for (int i = 0; i < fields.Length; i++) {
        var data = (await group.ReadColumnAsync(fields[i]).ConfigureAwait(false)).Data;
        foreach (var value in data) Append(columns[i], value);
      }
    }
    return new TabularData(fields.Select(f => f.Name), columns);
  }

  private static IList NewColumn(Type clrType) {
    var t = Nullable.GetUnderlyingType(clrType) ?? clrType;
    if (t == typeof(string)) return new List<string>();
    if (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly)) return new List<DateTime>();
    if (t == typeof(double) || t == typeof(float) || t == typeof(decimal) || t == typeof(int) || t == typeof(long)
        || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte) || t == typeof(ushort) || t == typeof(uint)
        || t == typeof(ulong) || t == typeof(bool))
      return new List<double>();
    throw new NotSupportedException($"Parquet column type {t.Name} is not supported.");
  }

  private static void Append(IList column, object? value) {
    switch (column) {
      case List<string> s: s.Add(value as string ?? ""); break;
      case List<DateTime> t:
        t.Add(value switch {
          DateTime d => d, DateTimeOffset o => o.UtcDateTime, DateOnly d => d.ToDateTime(TimeOnly.MinValue),
          _ => DateTime.MinValue
        });
        break;
      case List<double> d: d.Add(value switch { null => double.NaN, bool b => b ? 1 : 0, _ => Convert.ToDouble(value, CultureInfo.InvariantCulture) }); break;
    }
  }

  private static async Task WriteParquetAsync(TabularData table, string path) {
    var fields = table.Columns.Select((c, i) => (Field)(c switch {
      List<double> => new DataField<double>(table.Names[i]),
      List<DateTime> => new DataField<DateTime>(table.Names[i]),
      _ => new DataField<string>(table.Names[i])
    })).ToArray();
    var schema = new ParquetSchema(fields);
    using var stream = File.Create(path);
    using var writer = await ParquetWriter.CreateAsync(schema, stream).ConfigureAwait(false);
    using var group = writer.CreateRowGroup();
    for (int i = 0; i < fields.Length; i++) {
      Array values = table.Columns[i] switch {
        List<double> d => d.ToArray(), List<DateTime> t => t.ToArray(), List<string> s => s.ToArray(),
        _ => throw new InvalidOperationException()
      };
      await group.WriteColumnAsync(new DataColumn((DataField)fields[i], values)).ConfigureAwait(false);
    }
  }
}
