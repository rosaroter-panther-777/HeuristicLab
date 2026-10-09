using HeuristicLab.Core;
using HeuristicLab.Data;
using HeuristicLab.Optimization;

namespace HeuristicLab.Next.Runtime;

/// <summary>
/// Sets parameters from text, e.g. "PopulationSize=200", "Selector=TournamentSelector",
/// "Problem.BestKnownQuality=6110". Values that are convertible from text are parsed; operator
/// choices are matched against the parameter's valid values by name or type name.
/// </summary>
public static class ParameterEditor {
  public static void Apply(IParameterizedItem root, IEnumerable<string> assignments) {
    foreach (var assignment in assignments) {
      int eq = assignment.IndexOf('=');
      if (eq <= 0) throw new ArgumentException($"Expected Name=Value, got '{assignment}'.");
      Set(root, assignment[..eq].Trim(), assignment[(eq + 1)..].Trim());
    }
  }

  public static void Set(IParameterizedItem root, string path, string text) {
    var parts = path.Split('.');
    var target = root;
    foreach (var part in parts[..^1]) {
      target = Child(target, part) ?? throw new ArgumentException($"'{part}' in '{path}' is not a parameterized item.");
    }
    var name = parts[^1];
    if (!target.Parameters.TryGetValue(name, out var parameter))
      throw new ArgumentException($"{Describe(target)} has no parameter '{name}'. Parameters: " +
        string.Join(", ", target.Parameters.Select(p => p.Name).OrderBy(n => n)));

    if (parameter is IValueParameter { Value: IStringConvertibleValue value } && !value.ReadOnly) {
      if (!value.Validate(text, out var error) || !value.SetValue(text))
        throw new ArgumentException($"Invalid value '{text}' for {name}: {error}");
      return;
    }
    var validValues = parameter.GetType().GetInterfaces()
      .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConstrainedValueParameter<>));
    if (validValues != null) {
      var values = ((IEnumerable<IItem>)validValues.GetProperty("ValidValues")!.GetValue(parameter)!).ToList();
      var choice = values.FirstOrDefault(v => Matches(v, text))
        ?? throw new ArgumentException($"'{text}' is not a valid choice for {name}. Choices: " +
             string.Join(", ", values.Select(v => v.GetType().Name)));
      ((IValueParameter)parameter).Value = choice;
      return;
    }
    throw new ArgumentException($"Parameter {name} ({parameter.DataType.Name}) cannot be set from text.");
  }

  private static IParameterizedItem? Child(IParameterizedItem item, string name) {
    if (name == "Problem" && item is IAlgorithm { Problem: IParameterizedItem problem }) return problem;
    return item.Parameters.TryGetValue(name, out var p) && p is IValueParameter { Value: IParameterizedItem child } ? child : null;
  }

  private static bool Matches(IItem item, string text) =>
    string.Equals(item.GetType().Name, text, StringComparison.OrdinalIgnoreCase)
    || string.Equals(item.ItemName, text, StringComparison.OrdinalIgnoreCase)
    || (item is INamedItem named && string.Equals(named.Name, text, StringComparison.OrdinalIgnoreCase));

  private static string Describe(IParameterizedItem item) => item is INamedItem n ? n.Name : item.GetType().Name;
}
