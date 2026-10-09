using System.Reflection;
using HeuristicLab.Optimization;
using HeuristicLab.PluginInfrastructure;
using HeuristicLab.Problems.Instances;

namespace HeuristicLab.Next.Runtime;

/// <summary>A loadable problem instance, e.g. TSPLIB "berlin52".</summary>
public sealed record ProblemInstance(string Provider, string Name, string Description) {
  public string QualifiedName => $"{Provider}/{Name}";
}

/// <summary>
/// Benchmark instances from HeuristicLab's instance providers (TSPLIB, QAPLIB, regression
/// benchmarks, ...) and CSV import for regression/classification problems.
/// </summary>
public static class ProblemInstances {
  public static IReadOnlyList<ProblemInstance> For(IProblem problem) =>
    Providers(problem)
      .SelectMany(p => Descriptors(p.Provider, p.DataType)
        .Select(d => new ProblemInstance(p.Provider.Name, d.Name, d.Description ?? "")))
      .ToList();

  /// <summary>
  /// The library instance a problem was loaded from, found by its name (problems take the
  /// instance's name when loaded); null for problems not from a library (or renamed ones).
  /// </summary>
  public static ProblemInstance? Origin(IProblem problem) {
    var name = problem.Name;
    return Providers(problem)
      .SelectMany(p => Descriptors(p.Provider, p.DataType).Where(d => d.Name == name)
        .Select(d => new ProblemInstance(p.Provider.Name, d.Name, d.Description ?? "")))
      .FirstOrDefault();
  }

  /// <summary>Loads an instance by name or "Provider/Name" into the problem.</summary>
  public static ProblemInstance Load(IProblem problem, string name) {
    var matches = Providers(problem)
      .SelectMany(p => Descriptors(p.Provider, p.DataType).Select(d => (p.Provider, p.DataType, Descriptor: d)))
      .Where(m => string.Equals(m.Descriptor.Name, name, StringComparison.OrdinalIgnoreCase)
               || string.Equals($"{m.Provider.Name}/{m.Descriptor.Name}", name, StringComparison.OrdinalIgnoreCase))
      .ToList();
    if (matches.Count == 0) throw new ArgumentException($"No instance '{name}' for {problem.GetType().Name}.");
    if (matches.Count > 1)
      throw new ArgumentException($"Instance name '{name}' is ambiguous; use one of: " +
        string.Join(", ", matches.Select(m => $"{m.Provider.Name}/{m.Descriptor.Name}")));
    var (provider, dataType, descriptor) = matches[0];
    var data = ProviderInterface(dataType).GetMethod("LoadData")!.Invoke(provider, [descriptor]);
    ConsumerInterface(dataType).GetMethod("Load")!.Invoke(problem, [data]);
    return new ProblemInstance(provider.Name, descriptor.Name, descriptor.Description ?? "");
  }

  /// <summary>
  /// Imports a CSV or Parquet file into a regression, classification, time series or trading
  /// problem (see DataProblems): rows keep their order, the first trainingPercent train.
  /// </summary>
  public static string ImportCsv(IProblem problem, string path, string target, int trainingPercent = 66) =>
    DataProblems.Load(problem, path, target, null, trainingPercent);

  /// <summary>
  /// Providers whose data the problem consumes. Providers of exactly a consumed data type win;
  /// providers of derived data types (e.g. PTSP instances for a TSP) only count if there are none.
  /// </summary>
  private static IEnumerable<(IProblemInstanceProvider Provider, Type DataType)> Providers(IProblem problem) {
    HlRuntime.Initialize();
    var consumed = problem.GetType().GetInterfaces()
      .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IProblemInstanceConsumer<>))
      .Select(i => i.GetGenericArguments()[0]).ToHashSet();
    var compatible = ApplicationManager.Manager.GetInstances<IProblemInstanceProvider>()
      .Select(p => (Provider: p, DataType: DataTypeOf(p.GetType())!))
      .Where(p => p.DataType != null && ConsumerInterface(p.DataType).IsInstanceOfType(problem))
      .ToList();
    var exact = compatible.Where(p => consumed.Contains(p.DataType)).ToList();
    return (exact.Count > 0 ? exact : compatible).OrderBy(p => p.Provider.Name, StringComparer.OrdinalIgnoreCase);
  }

  private static IEnumerable<IDataDescriptor> Descriptors(IProblemInstanceProvider provider, Type dataType) =>
    (IEnumerable<IDataDescriptor>)ProviderInterface(dataType).GetMethod("GetDataDescriptors")!.Invoke(provider, null)!;

  private static Type? DataTypeOf(Type providerType) =>
    providerType.GetInterfaces()
      .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IProblemInstanceProvider<>))
      ?.GetGenericArguments()[0];

  private static Type ProviderInterface(Type dataType) => typeof(IProblemInstanceProvider<>).MakeGenericType(dataType);
  private static Type ConsumerInterface(Type dataType) => typeof(IProblemInstanceConsumer<>).MakeGenericType(dataType);
}
