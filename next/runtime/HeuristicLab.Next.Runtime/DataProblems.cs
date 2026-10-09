using HeuristicLab.Optimization;
using HeuristicLab.Problems.DataAnalysis;
using HeuristicLab.Problems.Instances;
using TradingProblemData = HeuristicLab.Problems.DataAnalysis.Trading.ProblemData;
using ITradingProblemData = HeuristicLab.Problems.DataAnalysis.Trading.IProblemData;

namespace HeuristicLab.Next.Runtime;

/// <summary>
/// Loads tabular data (CSV/Parquet) into data analysis problems: regression, classification,
/// time series prognosis and trading (target = price changes). Rows keep their order; the first
/// trainingPercent of rows train, the rest test - the right split for time-ordered data.
/// Inputs default to all numeric columns except the target that are not constant in training.
/// </summary>
public static class DataProblems {
  /// <param name="trainingStart">Rows at the top that are not used for training, e.g. the warm-up an
  /// autoregressive model needs (it reads values up to its time offset before each row).</param>
  public static string Load(IProblem problem, TabularData table, string target,
                            IReadOnlyCollection<string>? inputs = null, int trainingPercent = 66, int trainingStart = 0) {
    if (trainingPercent is < 1 or > 99) throw new ArgumentOutOfRangeException(nameof(trainingPercent));
    if (!table.NumericNames.Contains(target)) throw new ArgumentException($"Target '{target}' must be a numeric column. Columns: {string.Join(", ", table.Names)}");
    int trainingEnd = Math.Max(2, table.Rows * trainingPercent / 100);
    if (trainingStart < 0 || trainingStart >= trainingEnd - 1)
      throw new ArgumentException($"Training start {trainingStart} leaves no training rows (training ends at row {trainingEnd}).");
    var dataset = new Dataset(table.Names, table.Columns);
    var inputList = (inputs ?? DefaultInputs(table, target, trainingEnd)).ToList();
    foreach (var input in inputList)
      if (!table.NumericNames.Contains(input)) throw new ArgumentException($"Input '{input}' must be a numeric column.");

    // decide by the data types the problem consumes exactly: IProblemInstanceConsumer<in T> is
    // contravariant, so e.g. a regression problem would also "consume" time series data
    var consumed = problem.GetType().GetInterfaces()
      .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IProblemInstanceConsumer<>))
      .Select(i => i.GetGenericArguments()[0]).ToHashSet();
    string kind;
    if (consumed.Contains(typeof(ITradingProblemData))) {
      ((IProblemInstanceConsumer<ITradingProblemData>)problem).Load(
        Partition(new TradingProblemData(dataset, inputList.Append(target).Distinct(), target), table.Rows, trainingStart, trainingEnd));
      kind = "trading";
    } else if (consumed.Contains(typeof(ITimeSeriesPrognosisProblemData))) {
      ((IProblemInstanceConsumer<ITimeSeriesPrognosisProblemData>)problem).Load(
        Partition(new TimeSeriesPrognosisProblemData(dataset, inputList, target), table.Rows, trainingStart, trainingEnd));
      kind = "time series";
    } else if (consumed.Contains(typeof(IClassificationProblemData))) {
      ((IProblemInstanceConsumer<IClassificationProblemData>)problem).Load(
        Partition(new ClassificationProblemData(dataset, inputList, target), table.Rows, trainingStart, trainingEnd));
      kind = "classification";
    } else if (consumed.Contains(typeof(IRegressionProblemData))) {
      ((IProblemInstanceConsumer<IRegressionProblemData>)problem).Load(
        Partition(new RegressionProblemData(dataset, inputList, target), table.Rows, trainingStart, trainingEnd));
      kind = "regression";
    } else {
      throw new ArgumentException($"{problem.GetType().Name} cannot load tabular data " +
        "(regression, classification, time series and trading problems can).");
    }
    return $"{kind} data: {table.Rows} rows, target {target}, {inputList.Count} inputs, training rows {trainingStart}..{trainingEnd - 1}";
  }

  public static string Load(IProblem problem, string path, string target,
                            IReadOnlyCollection<string>? inputs = null, int trainingPercent = 66, int trainingStart = 0) =>
    Load(problem, DataFiles.Read(path), target, inputs, trainingPercent, trainingStart);

  private static IEnumerable<string> DefaultInputs(TabularData table, string target, int trainingEnd) =>
    table.NumericNames.Where(n => n != target).Where(n => {
      var values = table.Numbers(n).Take(trainingEnd).Where(double.IsFinite).ToList();
      return values.Count > 1 && values.Max() > values.Min();
    });

  private static T Partition<T>(T problemData, int rows, int trainingStart, int trainingEnd) where T : IDataAnalysisProblemData {
    problemData.TrainingPartition.Start = trainingStart;
    problemData.TrainingPartition.End = trainingEnd;
    problemData.TestPartition.Start = trainingEnd;
    problemData.TestPartition.End = rows;
    return problemData;
  }
}
