using HeuristicLab.Common;
using HeuristicLab.Core;
using HeuristicLab.Data;
using HeuristicLab.Optimization;

namespace HeuristicLab.Next.Runtime;

public sealed record RunOptions {
  /// <summary>Seed for every algorithm in the optimizer (also nested ones); null keeps the stored settings.</summary>
  public int? Seed { get; init; }
  /// <summary>How often progress is reported.</summary>
  public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(1);
  /// <summary>Optional wall-clock limit; the optimizer is stopped when it is reached.</summary>
  public TimeSpan? Timeout { get; init; }
  /// <summary>Labels copied into the report, e.g. sweep configuration or walk-forward fold.</summary>
  public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

/// <summary>
/// Progress snapshot: wall-clock time since the run started (monotonic, for plotting), the
/// optimizer's own execution time (updated in coarse steps), and its numeric results.
/// </summary>
public sealed record RunProgress(TimeSpan Elapsed, TimeSpan ExecutionTime, IReadOnlyDictionary<string, double> Values);

public enum RunOutcome { Completed, Stopped, Failed }

/// <summary>One recorded run: the parameters it used and the results it produced.</summary>
public sealed record RunRecord(
  string Name, IReadOnlyDictionary<string, ItemValue> Parameters, IReadOnlyDictionary<string, ItemValue> Results);

public sealed record RunReport(
  Provenance Provenance, string Optimizer, string OptimizerType, int? RequestedSeed,
  RunOutcome Outcome, string? Error, double ExecutionSeconds, IReadOnlyList<RunRecord> Runs,
  IReadOnlyDictionary<string, string>? Labels = null);

/// <summary>Runs an optimizer (algorithm, experiment, batch run) to completion, cancellation or failure.</summary>
public static class OptimizerRunner {
  public static async Task<RunReport> RunAsync(
      IOptimizer optimizer, RunOptions? options = null, string? inputFile = null,
      IProgress<RunProgress>? progress = null, CancellationToken cancellationToken = default) {
    HlRuntime.Initialize();
    options ??= new RunOptions();
    var provenance = Provenance.Capture(inputFile);
    if (options.Seed is int seed) ApplySeed(optimizer, seed);

    if (optimizer.ExecutionState != ExecutionState.Prepared) optimizer.Prepare(clearRuns: false);
    int runsBefore = optimizer.Runs.Count;

    Exception? error = null;
    void OnException(object? sender, EventArgs<Exception> e) => error ??= e.Value;
    optimizer.ExceptionOccurred += OnException;

    using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    if (options.Timeout is TimeSpan timeout) stopSource.CancelAfter(timeout);
    using var stopRegistration = stopSource.Token.Register(() => TryStop(optimizer));
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var progressTimer = progress == null ? null
      : new Timer(_ => progress.Report(Snapshot(optimizer, clock.Elapsed)), null, options.ProgressInterval, options.ProgressInterval);

    try {
      await Task.Run(() => optimizer.Start(CancellationToken.None), CancellationToken.None);
      // an exception or the stop request leaves the optimizer paused; stopping records its run
      if (optimizer.ExecutionState == ExecutionState.Paused) TryStop(optimizer);
    } catch (Exception e) {
      error ??= e;
    } finally {
      optimizer.ExceptionOccurred -= OnException;
      // waits for callbacks in flight, so no progress is reported after the final snapshot
      if (progressTimer != null) await progressTimer.DisposeAsync();
    }
    progress?.Report(Snapshot(optimizer, clock.Elapsed));

    var outcome = error != null ? RunOutcome.Failed
                : stopSource.IsCancellationRequested ? RunOutcome.Stopped
                : RunOutcome.Completed;
    var runs = optimizer.Runs.Skip(runsBefore).Select(ToRecord).ToList();
    return new RunReport(provenance, optimizer.Name, optimizer.GetType().FullName!, options.Seed, outcome,
      error?.ToString(), optimizer.ExecutionTime.TotalSeconds, runs, options.Labels);
  }

  /// <summary>Sets SetSeedRandomly=false and Seed=seed on all algorithms that have these parameters.</summary>
  public static void ApplySeed(IOptimizer optimizer, int seed) {
    foreach (var algorithm in Algorithms(optimizer)) {
      if (Value<BoolValue>(algorithm, "SetSeedRandomly") is BoolValue random) random.Value = false;
      if (Value<IntValue>(algorithm, "Seed") is IntValue value) value.Value = seed;
    }
  }

  public static RunRecord ToRecord(IRun run) => new(
    run.Name,
    run.Parameters.ToDictionary(p => p.Key, p => ItemValues.From(p.Value)),
    run.Results.ToDictionary(r => r.Key, r => ItemValues.From(r.Value)));

  private static IEnumerable<IAlgorithm> Algorithms(IOptimizer optimizer) =>
    new[] { optimizer }.Concat(optimizer.NestedOptimizers).OfType<IAlgorithm>();

  private static T? Value<T>(IParameterizedItem item, string name) where T : class, IItem =>
    item.Parameters.TryGetValue(name, out var parameter) && parameter is IValueParameter vp ? vp.Value as T : null;

  private static RunProgress Snapshot(IOptimizer optimizer, TimeSpan elapsed) {
    var values = new Dictionary<string, double>();
    if (optimizer is IAlgorithm algorithm) {
      try {
        foreach (var result in algorithm.Results.ToArray())
          if (ItemValues.AsNumber(result.Value) is double number) values[result.Name] = number;
      } catch (InvalidOperationException) {
        // results changed while being read by the running algorithm; next snapshot will catch up
      }
    }
    return new RunProgress(elapsed, optimizer.ExecutionTime, values);
  }

  private static void TryStop(IOptimizer optimizer) {
    try {
      if (optimizer.ExecutionState is ExecutionState.Started or ExecutionState.Paused) optimizer.Stop();
    } catch (InvalidOperationException) {
      // state changed concurrently (e.g. the run finished); nothing to stop
    }
  }
}
