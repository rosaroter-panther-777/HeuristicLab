using HeuristicLab.Core;
using HeuristicLab.Optimization;

namespace HeuristicLab.Next.Runtime;

public sealed record SweepResult(string Configuration, IReadOnlyList<string> Settings, BatchReport Batch);

/// <summary>
/// Full-factorial parameter sweeps: "PopulationSize=50,100" x "Selector=A,B" gives 4 configurations.
/// Every configuration runs with the same seeds (common random numbers), so differences come from
/// the parameters rather than from luck. Runs are labelled with their configuration.
/// </summary>
public static class Sweeps {
  /// <summary>Parses "Name=v1,v2,..." specs into the cartesian product of assignments.</summary>
  public static IReadOnlyList<IReadOnlyList<string>> Configurations(IEnumerable<string> specs) {
    IEnumerable<IReadOnlyList<string>> product = [[]];
    foreach (var spec in specs) {
      int eq = spec.IndexOf('=');
      if (eq <= 0) throw new ArgumentException($"Expected Name=value1,value2,..., got '{spec}'.");
      var name = spec[..eq].Trim();
      var values = Features.SplitList(spec[(eq + 1)..]);
      if (values.Count == 0) throw new ArgumentException($"No values in '{spec}'.");
      product = product.SelectMany(config => values.Select(v => (IReadOnlyList<string>)[.. config, $"{name}={v}"])).ToList();
    }
    return product.ToList();
  }

  public static async Task<IReadOnlyList<SweepResult>> RunAsync(
      IOptimizer template, IEnumerable<string> specs, int repetitions, int? baseSeed = null, int parallelism = 1,
      RunOptions? options = null, string? inputFile = null,
      Action<string, int, RunReport>? onRunCompleted = null, CancellationToken cancellationToken = default) {
    if (template is not IParameterizedItem) throw new ArgumentException("Sweeps need an algorithm (experiments have no parameters of their own).");
    var configurations = Configurations(specs);
    // validate every assignment before running anything
    foreach (var settings in configurations) ParameterEditor.Apply((IParameterizedItem)template.Clone(), settings);

    int seed0 = baseSeed ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, int.MaxValue - repetitions);
    var results = new List<SweepResult>();
    foreach (var settings in configurations) {
      if (cancellationToken.IsCancellationRequested) break;
      var configured = (IOptimizer)template.Clone();
      ParameterEditor.Apply((IParameterizedItem)configured, settings);
      var name = string.Join("; ", settings);
      var labels = new Dictionary<string, string>(options?.Labels ?? new Dictionary<string, string>()) { ["config"] = name };
      foreach (var s in settings) { int eq = s.IndexOf('='); labels[s[..eq]] = s[(eq + 1)..]; }
      var batch = await BatchRunner.RepeatAsync(BatchRunner.Copies(configured), repetitions, seed0, parallelism,
        (options ?? new RunOptions()) with { Labels = labels }, inputFile,
        (i, r) => onRunCompleted?.Invoke(name, i, r), cancellationToken).ConfigureAwait(false);
      results.Add(new SweepResult(name, settings, batch));
    }
    return results;
  }
}
