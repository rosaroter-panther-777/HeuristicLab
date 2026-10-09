using System.Globalization;
using System.Reflection;
using System.Text;
using HeuristicLab.Optimization;
using HeuristicLab.PluginInfrastructure;
using HeuristicLab.Problems.DataAnalysis;
using HeuristicLab.Problems.Instances;
using HeuristicLab.Problems.Instances.DataAnalysis;

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
  /// Imports a CSV file (header row with variable names; separator and number format detected)
  /// into a regression or classification problem. Rows are not shuffled (HeuristicLab's shuffle
  /// is unseeded); the first trainingPercent of rows form the training partition.
  /// </summary>
  public static void ImportCsv(IProblem problem, string path, string target, int trainingPercent = 66) {
    if (trainingPercent is < 1 or > 99) throw new ArgumentOutOfRangeException(nameof(trainingPercent));
    TableFileParser.DetermineFileFormat(path, out var numberFormat, out var dateFormat, out var separator);
    var format = new DataAnalysisCSVFormat {
      Separator = separator, NumberFormatInfo = numberFormat, DateTimeFormatInfo = dateFormat,
      VariableNamesAvailable = true, Encoding = Encoding.UTF8
    };
    switch (problem) {
      case IProblemInstanceConsumer<IRegressionProblemData> regression:
        regression.Load(new RegressionCSVInstanceProvider().ImportData(path,
          new RegressionImportType { TargetVariable = target, TrainingPercentage = trainingPercent, Shuffle = false }, format));
        break;
      case IProblemInstanceConsumer<IClassificationProblemData> classification:
        classification.Load(new ClassificationCSVInstanceProvider().ImportData(path,
          new ClassificationImportType { TargetVariable = target, TrainingPercentage = trainingPercent, Shuffle = false }, format));
        break;
      default:
        throw new ArgumentException($"{problem.GetType().Name} cannot import CSV data (regression and classification problems can).");
    }
  }

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
