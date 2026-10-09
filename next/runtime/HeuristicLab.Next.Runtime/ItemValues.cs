using System.Collections;
using HeuristicLab.Analysis;
using HeuristicLab.Core;
using HeuristicLab.Data;

namespace HeuristicLab.Next.Runtime;

/// <summary>
/// A HeuristicLab item as a plain value. Value holds a number, bool, string, array, matrix
/// (array of rows) or TableValue; items without a plain form keep their text.
/// </summary>
public sealed record ItemValue(string Type, object? Value, string? Text = null);

/// <summary>Value of a DataTable result: named rows of values, plus the table's axis titles.</summary>
public sealed record TableValue(string Name, string XAxisTitle, string YAxisTitle, IReadOnlyDictionary<string, double[]> Rows);

public static class ItemValues {
  public static ItemValue From(IItem? item) {
    if (item == null) return new ItemValue("null", null);
    var type = item.GetType().Name;
    switch (item) {
      case PercentValue p: return new ItemValue(type, p.Value);
      case DoubleValue d: return new ItemValue(type, d.Value);
      case IntValue i: return new ItemValue(type, i.Value);
      case BoolValue b: return new ItemValue(type, b.Value);
      case TimeSpanValue t: return new ItemValue(type, t.Value.TotalSeconds, t.Value.ToString());
      case StringValue s: return new ItemValue(type, s.Value);
      case StringArray sa: return new ItemValue(type, sa.ToArray());
      case StringMatrix sm: return new ItemValue(type, Rows(sm.Rows, sm.Columns, (r, c) => sm[r, c]));
      case DataTable table:
        return new ItemValue(type, new TableValue(table.Name, table.VisualProperties.XAxisTitle ?? "",
          table.VisualProperties.YAxisTitle ?? "", table.Rows.ToDictionary(r => r.Name, r => r.Values.ToArray())));
    }
    var generic = GenericBase(item.GetType());
    if (generic == typeof(ValueTypeValue<>))
      return new ItemValue(type, item.GetType().GetProperty("Value")!.GetValue(item));
    if (generic == typeof(ValueTypeArray<>))
      return new ItemValue(type, ((IEnumerable)item).Cast<object>().ToArray());
    if (generic == typeof(ValueTypeMatrix<>)) {
      dynamic m = item;
      int rows = m.Rows, columns = m.Columns;
      return new ItemValue(type, Rows<object>(rows, columns, (r, c) => m[r, c]));
    }
    return new ItemValue(type, null, item.ToString());
  }

  /// <summary>Numeric value of scalar items (for progress display), otherwise null.</summary>
  public static double? AsNumber(IItem? item) => From(item).Value switch {
    double d => d, int i => i, long l => l, float f => f, _ => null
  };

  private static T[][] Rows<T>(int rows, int columns, Func<int, int, T> get) =>
    Enumerable.Range(0, rows).Select(r => Enumerable.Range(0, columns).Select(c => get(r, c)).ToArray()).ToArray();

  private static Type? GenericBase(Type? type) {
    for (; type != null; type = type.BaseType)
      if (type.IsGenericType) return type.GetGenericTypeDefinition();
    return null;
  }
}
