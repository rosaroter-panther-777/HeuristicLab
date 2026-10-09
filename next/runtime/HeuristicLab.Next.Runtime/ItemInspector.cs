using HeuristicLab.Core;
using HeuristicLab.Data;
using HeuristicLab.Operators;
using HeuristicLab.Optimization;
using HeuristicLab.PluginInfrastructure;

namespace HeuristicLab.Next.Runtime;

/// <summary>How a front end shows and edits a value.</summary>
public enum ValueKind {
  /// <summary>No value set.</summary>
  None,
  Bool,
  /// <summary>Scalar convertible to and from text (numbers, strings, percentages, ...).</summary>
  Text,
  /// <summary>One-dimensional values (arrays, permutations, binary vectors); cells editable as text.</summary>
  Array,
  /// <summary>Two-dimensional values (coordinates, distance matrices); cells editable as text.</summary>
  Matrix,
  /// <summary>Ordered items, each enabled or not (e.g. the analyzers of a MultiAnalyzer).</summary>
  CheckedList,
  /// <summary>An item with its own parameters (operators, evaluators, ...).</summary>
  Parameterized,
  /// <summary>Anything else: shown as text, read-only.</summary>
  Other
}

/// <summary>
/// Front-end-neutral access to HeuristicLab items for inspection and editing: value kinds, typed
/// parameters, checked item lists, operator graphs and engines. Front ends build their views on
/// this instead of on the WinForms-era view classes.
/// </summary>
public static class ItemInspector {
  public static ValueKind KindOf(IItem? value) => value switch {
    null => ValueKind.None,
    BoolValue => ValueKind.Bool,
    IStringConvertibleValue => ValueKind.Text,
    IStringConvertibleArray => ValueKind.Array,
    IStringConvertibleMatrix => ValueKind.Matrix,
    _ when CheckedList.TryCreate(value) != null => ValueKind.CheckedList,
    IParameterizedItem => ValueKind.Parameterized,
    _ => ValueKind.Other
  };

  /// <summary>Short text for lists: the value of scalars, size of arrays and matrices, name of items.</summary>
  public static string Summary(IItem? value) => value switch {
    null => "(none)",
    IStringConvertibleValue v => v.GetValue(),
    IStringConvertibleArray a => $"{value.ItemName} [{a.Length}]",
    IStringConvertibleMatrix m => $"{value.ItemName} [{m.Rows}×{m.Columns}]",
    INamedItem n => n.Name,
    _ => value.ItemName
  };

  /// <summary>Readable type name: "IMoveGenerator", "ValueParameter&lt;IntValue&gt;".</summary>
  public static string TypeName(Type type) =>
    !type.IsGenericType ? type.Name
    : $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";

  /// <summary>Visible parameters in name order.</summary>
  public static IReadOnlyList<IParameter> Parameters(IParameterizedItem item) =>
    item.Parameters.Where(p => !p.Hidden).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

  /// <summary>Valid values of a selection parameter (e.g. the move generators that fit the problem), else null.</summary>
  public static IReadOnlyList<IItem>? Choices(IParameter parameter) {
    var constrained = parameter.GetType().GetInterfaces()
      .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConstrainedValueParameter<>));
    return constrained == null ? null : ((IEnumerable<IItem>)constrained.GetProperty("ValidValues")!.GetValue(parameter)!).ToList();
  }

  /// <summary>Value of a parameter as stored (not looked up in a scope).</summary>
  public static IItem? Value(IParameter parameter) => (parameter as IValueParameter)?.Value;

  /// <summary>Sets a scalar from text; returns an error message or null.</summary>
  public static string? SetText(IItem value, string text) {
    if (value is not IStringConvertibleValue v || v.ReadOnly) return "This value cannot be edited.";
    if (!v.Validate(text, out var error)) return error;
    return v.SetValue(text) ? null : "Invalid value.";
  }

  public static string? SetCell(IItem value, int row, int column, string text) {
    switch (value) {
      case IStringConvertibleArray a when !a.ReadOnly:
        return a.Validate(text, out var e1) ? (a.SetValue(text, row) ? null : "Invalid value.") : e1;
      case IStringConvertibleMatrix m when !m.ReadOnly:
        return m.Validate(text, out var e2) ? (m.SetValue(text, row, column) ? null : "Invalid value.") : e2;
      default:
        return "This value cannot be edited.";
    }
  }

  public static IReadOnlyList<string> Cells(IStringConvertibleArray a) => Enumerable.Range(0, a.Length).Select(a.GetValue).ToList();

  public static IReadOnlyList<IReadOnlyList<string>> Cells(IStringConvertibleMatrix m) =>
    Enumerable.Range(0, m.Rows).Select(r => (IReadOnlyList<string>)Enumerable.Range(0, m.Columns).Select(c => m.GetValue(r, c)).ToList()).ToList();

  // ---- operator graphs

  public sealed record GraphNode(IOperator Operator, int Layer, int Row);
  public sealed record GraphEdge(IOperator From, IOperator To, string Label);
  public sealed record GraphLayout(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges, IOperator? Initial);

  /// <summary>The operator graph of an algorithm or an algorithm operator (e.g. a main loop), if it has one.</summary>
  public static OperatorGraph? GraphOf(IItem item) => item switch {
    EngineAlgorithm a => a.OperatorGraph,
    AlgorithmOperator o => o.OperatorGraph,
    _ => null
  };

  /// <summary>
  /// Nodes in layers by distance from the initial operator (unreachable ones last), and the
  /// successor edges: operator-valued parameters pointing to operators of the graph.
  /// </summary>
  public static GraphLayout Layout(OperatorGraph graph) {
    var operators = graph.Operators.ToList();
    var inGraph = operators.ToHashSet();
    var edges = new List<GraphEdge>();
    foreach (var op in operators)
      foreach (var p in op.Parameters.OfType<IValueParameter>())
        if (p.Value is IOperator target && inGraph.Contains(target)) edges.Add(new GraphEdge(op, target, p.Name));
    var layer = new Dictionary<IOperator, int>();
    var queue = new Queue<IOperator>();
    if (graph.InitialOperator != null && inGraph.Contains(graph.InitialOperator)) { layer[graph.InitialOperator] = 0; queue.Enqueue(graph.InitialOperator); }
    while (queue.Count > 0) {
      var op = queue.Dequeue();
      foreach (var e in edges.Where(e => e.From == op && !layer.ContainsKey(e.To))) { layer[e.To] = layer[op] + 1; queue.Enqueue(e.To); }
    }
    int unreachable = layer.Count == 0 ? 0 : layer.Values.Max() + 1;
    foreach (var op in operators.Where(o => !layer.ContainsKey(o))) layer[op] = unreachable;
    var nodes = operators.GroupBy(o => layer[o]).OrderBy(g => g.Key)
      .SelectMany(g => g.Select((o, i) => new GraphNode(o, g.Key, i))).ToList();
    return new GraphLayout(nodes, edges, graph.InitialOperator);
  }

  // ---- engines

  /// <summary>Engines an algorithm can run on (sequential, parallel, ...), from the loaded plugins.</summary>
  public static IReadOnlyList<CatalogEntry> Engines() {
    HlRuntime.Initialize();
    return ApplicationManager.Manager.GetTypes(typeof(IEngine))
      .Where(t => t.IsPublic && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
      .Select(t => new CatalogEntry(ItemAttribute.GetName(t), ItemAttribute.GetDescription(t), t.FullName!, t))
      .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
  }
}

/// <summary>A checked item list (e.g. MultiAnalyzer.Operators) without its generic type.</summary>
public sealed class CheckedList {
  private readonly object target;
  private readonly Type checkedInterface, listInterface, collectionInterface;

  private CheckedList(object target, Type checkedInterface) {
    this.target = target;
    this.checkedInterface = checkedInterface;
    ElementType = checkedInterface.GetGenericArguments()[0];
    listInterface = typeof(IList<>).MakeGenericType(ElementType);
    collectionInterface = typeof(ICollection<>).MakeGenericType(ElementType);
  }

  /// <summary>The checked list itself, or the operator list of an item that has one (MultiAnalyzer, MultiOperator).</summary>
  public static CheckedList? TryCreate(IItem? item) {
    if (item == null) return null;
    // "Operators" is redeclared (new) in CheckedMultiOperator<T>: take the most derived declaration
    var operators = item.GetType().GetProperties().Where(p => p.Name == "Operators" && p.GetIndexParameters().Length == 0)
      .OrderBy(p => Depth(item.GetType(), p.DeclaringType!)).FirstOrDefault();
    var candidate = Interface(item.GetType()) != null ? item : operators?.GetValue(item);
    var iface = candidate == null ? null : Interface(candidate.GetType());
    return iface == null ? null : new CheckedList(candidate!, iface);
  }

  private static int Depth(Type type, Type declaring) {
    int depth = 0;
    for (var t = type; t != null && t != declaring; t = t.BaseType) depth++;
    return depth;
  }

  private static Type? Interface(Type type) =>
    type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICheckedItemList<>));

  public Type ElementType { get; }
  public int Count => (int)collectionInterface.GetProperty("Count")!.GetValue(target)!;
  public IItem this[int index] {
    get => (IItem)listInterface.GetProperty("Item")!.GetValue(target, [index])!;
    private set => listInterface.GetProperty("Item")!.SetValue(target, value, [index]);
  }
  public IReadOnlyList<IItem> Items => Enumerable.Range(0, Count).Select(i => this[i]).ToList();

  public bool IsChecked(int index) => (bool)checkedInterface.GetMethod("ItemChecked", [typeof(int)])!.Invoke(target, [index])!;
  public void SetChecked(int index, bool value) => checkedInterface.GetMethod("SetItemCheckedState", [typeof(int), typeof(bool)])!.Invoke(target, [index, value]);

  /// <summary>Moves an item, keeping every item's checked state.</summary>
  public void Move(int from, int to) {
    if (from == to || from < 0 || to < 0 || from >= Count || to >= Count) return;
    var states = Enumerable.Range(0, Count).Select(i => (Item: this[i], Checked: IsChecked(i))).ToList();
    var moved = states[from];
    states.RemoveAt(from);
    states.Insert(to, moved);
    for (int i = 0; i < Count; i++) this[i] = states[i].Item;
    for (int i = 0; i < Count; i++) SetChecked(i, states[i].Checked);
  }

  public void RemoveAt(int index) => listInterface.GetMethod("RemoveAt")!.Invoke(target, [index]);

  /// <summary>Item types that could be added (from the loaded plugins).</summary>
  public IReadOnlyList<CatalogEntry> AddableTypes() {
    HlRuntime.Initialize();
    return ApplicationManager.Manager.GetTypes(ElementType)
      .Where(t => t.IsPublic && !t.IsAbstract && !t.ContainsGenericParameters && t.GetConstructor(Type.EmptyTypes) != null)
      .Select(t => new CatalogEntry(ItemAttribute.GetName(t), ItemAttribute.GetDescription(t), t.FullName!, t))
      .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
  }

  public void Add(CatalogEntry entry) => collectionInterface.GetMethod("Add")!.Invoke(target, [entry.CreateInstance()]);
}
