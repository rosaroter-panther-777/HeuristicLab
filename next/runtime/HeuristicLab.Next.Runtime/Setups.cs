using HeuristicLab.Optimization;

namespace HeuristicLab.Next.Runtime;

/// <summary>What to build: algorithm, optional problem, and either a benchmark instance or a data file.</summary>
public sealed record SetupRequest(string Algorithm) {
  public string? Problem { get; init; }
  public string? Instance { get; init; }
  /// <summary>CSV or Parquet file for regression, classification, time series or trading problems.</summary>
  public string? DataFile { get; init; }
  public string? Target { get; init; }
  public IReadOnlyCollection<string>? Inputs { get; init; }
  public int TrainingPercent { get; init; } = 66;
  /// <summary>Rows at the top excluded from training (warm-up, e.g. for autoregressive models).</summary>
  public int TrainingStart { get; init; }
  /// <summary>Parameter assignments, e.g. "PopulationSize=200".</summary>
  public IReadOnlyList<string> Settings { get; init; } = [];
  public string? Name { get; init; }
}

public sealed record SetupResult(IAlgorithm Algorithm, IReadOnlyList<string> Messages);

/// <summary>Builds ready-to-run algorithms from the catalog (shared by the CLI and Studio).</summary>
public static class Setups {
  /// <exception cref="ArgumentException">With a message meant for the user.</exception>
  public static SetupResult Create(SetupRequest request) {
    var messages = new List<string>();
    var algorithmEntry = Catalog.Find(Catalog.Algorithms(), request.Algorithm)
      ?? throw new ArgumentException($"Unknown algorithm '{request.Algorithm}'.");
    var algorithm = (IAlgorithm)algorithmEntry.CreateInstance();

    if (request.Problem != null) {
      var problemEntry = Catalog.Find(Catalog.Problems(), request.Problem)
        ?? throw new ArgumentException($"Unknown problem '{request.Problem}'.");
      var problem = (IProblem)problemEntry.CreateInstance();
      if (!algorithm.ProblemType.IsInstanceOfType(problem))
        throw new ArgumentException($"{algorithmEntry.Name} cannot solve {problemEntry.Name} (needs {algorithm.ProblemType.Name}).");
      algorithm.Problem = problem;
    }
    if ((request.Instance != null || request.DataFile != null) && algorithm.Problem == null)
      throw new ArgumentException("A benchmark instance or data file needs a problem.");
    if (request.Instance != null && request.DataFile != null)
      throw new ArgumentException("Use either a benchmark instance or a data file, not both.");

    if (request.Instance != null)
      messages.Add($"Loaded instance {ProblemInstances.Load(algorithm.Problem!, request.Instance).QualifiedName}");
    if (request.DataFile != null) {
      if (request.Target == null) throw new ArgumentException("A data file needs a target column.");
      messages.Add($"Loaded {Path.GetFileName(request.DataFile)}: " +
        DataProblems.Load(algorithm.Problem!, request.DataFile, request.Target, request.Inputs, request.TrainingPercent, request.TrainingStart));
    }
    ParameterEditor.Apply(algorithm, request.Settings);
    if (request.Name != null) algorithm.Name = request.Name;
    return new SetupResult(algorithm, messages);
  }

  /// <summary>Problems the algorithm can solve (for pickers).</summary>
  public static IReadOnlyList<CatalogEntry> ProblemsFor(CatalogEntry algorithm) {
    var instance = (IAlgorithm)algorithm.CreateInstance();
    return Catalog.Problems().Where(p => instance.ProblemType.IsAssignableFrom(p.Type)).ToList();
  }
}
