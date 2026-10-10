using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using HeuristicLab.Common;
using HeuristicLab.Core;
using HeuristicLab.Data;
using HeuristicLab.Encodings.SymbolicExpressionTreeEncoding;
using HeuristicLab.Optimization;
using HeuristicLab.Problems.VehicleRouting.Interfaces;

namespace HeuristicLab.Next.Runtime.Tracing;

/// <summary>
/// One solution the algorithm created: by which operator, in which iteration and step, from which
/// parents (the parents' ids: crossover parents, or the solution a mutation or move changed).
/// Quality is filled in when the solution is evaluated. Solution is a copy taken at creation; it is
/// null once the trace's storage limit is reached (the step is still recorded).
/// </summary>
public sealed class TracedSolution(int id, int iteration, string @operator, IItem? solution, IReadOnlyList<int> parents, double seconds) {
  public int Id { get; } = id;
  /// <summary>Also the step number: every created solution is one step.</summary>
  public int Step => Id;
  public int Iteration { get; } = iteration;
  public string Operator { get; } = @operator;
  public IItem? Solution { get; } = solution;
  public IReadOnlyList<int> Parents { get; } = parents;
  /// <summary>Seconds since the run started.</summary>
  public double Seconds { get; } = seconds;
  public double? Quality { get; internal set; }
}

/// <param name="Number">
/// 0 is the initialization; iteration n ends with the n-th run of the algorithm's analyzer after
/// it, which is how HeuristicLab indexes its own quality charts (else the Generations/Iterations
/// counter is used).
/// </param>
/// <param name="FirstStep">Id of the first solution created in it.</param>
/// <param name="Start">Seconds since the run started.</param>
public sealed record TracedIteration(int Number, int FirstStep, int StepCount, double Start, double Duration,
                                     double? Best, double? Average, double? Worst, double? BestSoFar);

/// <summary>
/// The record of a detailed analysis run: every solution created (with operator, parents, quality,
/// iteration and time) and the iterations with their timing. Written by the running algorithm,
/// readable at any time (methods return snapshots).
/// </summary>
public sealed class Trace {
  private readonly object sync = new();
  private readonly List<TracedSolution> solutions = [];
  private readonly List<(int Number, int FirstStep, double Start)> iterations = [];
  private double end;

  internal Trace(IAlgorithm algorithm, bool maximization, int storageLimit) {
    Algorithm = algorithm;
    Maximization = maximization;
    StorageLimit = storageLimit;
  }

  /// <summary>The copy of the algorithm that was run (its results and run are those of the traced run).</summary>
  public IAlgorithm Algorithm { get; }
  public IProblem? Problem => Algorithm.Problem;
  public bool Maximization { get; }
  /// <summary>Solutions stored as copies; later ones are recorded without their copy.</summary>
  public int StorageLimit { get; }
  /// <summary>Scope variable that holds a solution (found when the solution creator runs).</summary>
  public string? SolutionName { get; internal set; }
  public bool IsFinished { get; private set; }

  public int SolutionCount { get { lock (sync) return solutions.Count; } }

  public TracedSolution Solution(int id) { lock (sync) return solutions[id]; }

  public IReadOnlyList<TracedSolution> Solutions() { lock (sync) return solutions.ToArray(); }

  /// <summary>Solutions created in iterations from..to (inclusive).</summary>
  public IReadOnlyList<TracedSolution> Solutions(int fromIteration, int toIteration) {
    lock (sync) return solutions.Where(s => s.Iteration >= fromIteration && s.Iteration <= toIteration).ToArray();
  }

  /// <summary>Iterations with timing and the qualities of the solutions created in them.</summary>
  public IReadOnlyList<TracedIteration> Iterations() {
    lock (sync) {
      var result = new List<TracedIteration>();
      double? bestSoFar = null;
      for (int i = 0; i < iterations.Count; i++) {
        var (number, first, start) = iterations[i];
        int next = i + 1 < iterations.Count ? iterations[i + 1].FirstStep : solutions.Count;
        double stop = i + 1 < iterations.Count ? iterations[i + 1].Start : (IsFinished ? end : Math.Max(start, end));
        var qualities = solutions.Skip(first).Take(next - first).Where(s => s.Quality.HasValue).Select(s => s.Quality!.Value).ToList();
        double? best = qualities.Count == 0 ? null : Maximization ? qualities.Max() : qualities.Min();
        if (best.HasValue) bestSoFar = bestSoFar == null ? best : Maximization ? Math.Max(bestSoFar.Value, best.Value) : Math.Min(bestSoFar.Value, best.Value);
        result.Add(new TracedIteration(number, first, next - first, start, Math.Max(0, stop - start), best,
          qualities.Count == 0 ? null : qualities.Average(), qualities.Count == 0 ? null : Maximization ? qualities.Min() : qualities.Max(), bestSoFar));
      }
      return result;
    }
  }

  /// <summary>The best solution created in an iteration (by quality), else its last one.</summary>
  public TracedSolution? Representative(int iteration) {
    lock (sync) {
      var created = solutions.Where(s => s.Iteration == iteration).ToList();
      var rated = created.Where(s => s.Quality.HasValue).ToList();
      if (rated.Count > 0) return Maximization ? rated.MaxBy(s => s.Quality) : rated.MinBy(s => s.Quality);
      return created.LastOrDefault();
    }
  }

  internal TracedSolution Add(int iteration, string op, IItem? copy, IReadOnlyList<int> parents, double seconds) {
    lock (sync) {
      var solution = new TracedSolution(solutions.Count, iteration, op, solutions.Count < StorageLimit ? copy : null, parents, seconds);
      solutions.Add(solution);
      end = seconds;
      return solution;
    }
  }

  internal bool StoresCopies { get { lock (sync) return solutions.Count < StorageLimit; } }

  internal void SetQuality(int id, double quality) { lock (sync) solutions[id].Quality = quality; }

  internal void BeginIteration(int number, double seconds) {
    lock (sync) {
      if (iterations.Count > 0 && iterations[^1].Number == number) return;
      iterations.Add((number, solutions.Count, seconds));
      end = seconds;
    }
  }

  internal void Finish(double seconds) {
    lock (sync) { end = seconds; IsFinished = true; }
  }
}

public sealed record DetailedRunOptions {
  public int? Seed { get; init; }
  public TimeSpan? Timeout { get; init; }
  /// <summary>Solutions kept as copies for visualization (memory); steps beyond are still counted and charted.</summary>
  public int StorageLimit { get; init; } = 200_000;
}

/// <summary>
/// A detailed analysis run: a copy of an operator-based algorithm runs on a recording engine that
/// executes it like the sequential engine and, after every operator, notes which solution in the
/// operator's scope is new or changed. Algorithms without an operator graph cannot be traced.
/// </summary>
public static class DetailedRun {
  /// <summary>
  /// Operator-based algorithms on heuristic optimization problems (whose solutions live in scopes);
  /// not data analysis algorithms such as Gaussian process regression, which fit a model in one step.
  /// </summary>
  public static bool CanTrace(IAlgorithm algorithm) => algorithm is EngineAlgorithm && algorithm.Problem is IHeuristicOptimizationProblem;

  public static string? WhyNot(IAlgorithm algorithm) =>
    algorithm.Problem == null ? $"{algorithm.Name} has no problem."
    : algorithm is not EngineAlgorithm ? $"{algorithm.Name} has no operator graph, so its steps cannot be recorded."
    : algorithm.Problem is not IHeuristicOptimizationProblem ? $"{algorithm.Name} fits a model in one step; it creates no candidate solutions to record."
    : null;

  /// <summary>Starts the run; the trace is available immediately (it fills while the run goes on).</summary>
  public static (Trace Trace, Task<RunReport> Run) Start(IAlgorithm algorithm, DetailedRunOptions? options = null, CancellationToken cancellationToken = default) {
    HlRuntime.Initialize();
    options ??= new DetailedRunOptions();
    if (WhyNot(algorithm) is string reason) throw new NotSupportedException(reason);
    var copy = (EngineAlgorithm)algorithm.Clone();
    copy.Prepare(true);
    var problem = copy.Problem!;
    bool maximization = problem.Parameters.TryGetValue("Maximization", out var m) && m is IValueParameter { Value: BoolValue { Value: true } };
    var trace = new Trace(copy, maximization, options.StorageLimit);
    var analyzer = ((IParameterizedItem)copy).Parameters.TryGetValue("Analyzer", out var a) && a is IValueParameter { Value: IOperator op } ? op : null;
    copy.Engine = new RecordingEngine(new Recorder(trace, problem, analyzer));
    var run = OptimizerRunner.RunAsync(copy, new RunOptions { Seed = options.Seed, Timeout = options.Timeout }, cancellationToken: cancellationToken)
      .ContinueWith(t => { trace.Finish(copy.ExecutionTime.TotalSeconds); return t.Result; }, TaskScheduler.Default);
    return (trace, run);
  }
}

/// <summary>Sequential execution (as HeuristicLab's sequential engine) with a look at every operator's scope afterwards.</summary>
internal sealed class RecordingEngine : Engine {
  private readonly Recorder recorder;

  public RecordingEngine(Recorder recorder) => this.recorder = recorder;
  private RecordingEngine(RecordingEngine original, Cloner cloner) : base(original, cloner) => recorder = original.recorder;
  public override IDeepCloneable Clone(Cloner cloner) => new RecordingEngine(this, cloner);

  protected override void Run(CancellationToken cancellationToken) {
    while (ExecutionStack.Count > 0) {
      cancellationToken.ThrowIfCancellationRequested();
      var next = ExecutionStack.Pop();
      if (next is OperationCollection collection) {
        for (int i = collection.Count - 1; i >= 0; i--)
          if (collection[i] != null) ExecutionStack.Push(collection[i]);
      } else if (next is IAtomicOperation operation) {
        var before = recorder.Before(operation);
        try {
          next = operation.Operator.Execute((IExecutionContext)operation, cancellationToken);
        } catch (Exception ex) {
          ExecutionStack.Push(operation);
          if (ex is OperationCanceledException) throw;
          throw new OperatorExecutionException(operation.Operator, ex);
        }
        recorder.After(operation, before);
        if (next != null) ExecutionStack.Push(next);
      }
    }
  }
}

/// <summary>
/// Notices solutions: a scope's solution variable that is new or whose content changed is a created
/// solution. Scopes copied by selection carry known content and are not new solutions; a scope
/// whose sub-scopes hold solutions (crossover) gets those as parents, a changed solution (mutation,
/// move) the solution it replaced.
/// </summary>
internal sealed class Recorder(Trace trace, IProblem problem, IOperator? analyzer) {
  private sealed class ScopeState { public object? Value; public long? Fingerprint; public int Id; }

  private static readonly string[] CounterNames = ["Generations", "Iterations"];
  private readonly ConditionalWeakTable<IScope, ScopeState> states = new();
  private readonly Dictionary<long, int> byContent = [];
  private readonly ConditionalWeakTable<object, StrongBox<int>> byReference = new();
  private readonly Stopwatch clock = Stopwatch.StartNew();
  private readonly string qualityName = QualityName(problem);
  private string? solutionName = EncodingName(problem);
  private IScope? global;
  private int iteration;
  private double? pendingStart;
  private (IScope Scope, HashSet<string> Names)? creation;

  private static string QualityName(IProblem problem) {
    if (problem.Parameters.TryGetValue("Evaluator", out var p) && p is IValueParameter { Value: { } evaluator }) {
      if (evaluator is ISingleObjectiveEvaluator single) return single.QualityParameter.ActualName;
      if (evaluator is IMultiObjectiveEvaluator multi) return multi.QualitiesParameter.ActualName;
    }
    return "Quality";
  }

  /// <summary>
  /// Basic problems name the variable after their encoding; others (and multi-encodings, whose
  /// parts are separate variables: the first new one is traced) are found when their creator runs.
  /// </summary>
  private static string? EncodingName(IProblem problem) =>
    problem.Parameters.TryGetValue("Encoding", out var p) && p is IValueParameter { Value: IEncoding encoding } && encoding is not MultiEncoding
      ? encoding.Name : null;

  public HashSet<string>? Before(IAtomicOperation operation) =>
    solutionName == null && problem is IHeuristicOptimizationProblem h && ReferenceEquals(operation.Operator, h.SolutionCreator)
      ? operation.Scope.Variables.Select(v => v.Name).ToHashSet()
      : null;

  public void After(IAtomicOperation operation, HashSet<string>? namesBefore) {
    var scope = operation.Scope;
    if (global == null) { global = scope; while (global.Parent != null) global = global.Parent; }
    if (trace.SolutionCount == 0) trace.BeginIteration(0, clock.Elapsed.TotalSeconds);
    if (analyzer != null) {
      // the analyzer closes an iteration; the next begins with the next solution (none after the last)
      if (ReferenceEquals(operation.Operator, analyzer)) pendingStart ??= clock.Elapsed.TotalSeconds;
    } else UpdateIteration();
    // the creator may only schedule other operators (a multi-encoding creator runs one per part):
    // watch its scope until a variable appears
    if (namesBefore != null && solutionName == null) creation ??= (scope, namesBefore);
    if (creation is var (created, before) && solutionName == null) {
      solutionName = created.Variables.Select(v => v.Name).FirstOrDefault(n => !before.Contains(n) && n != qualityName);
      trace.SolutionName = solutionName;
    }
    if (solutionName == null) return;
    trace.SolutionName ??= solutionName;
    // operators find their variables by lookup up the scope tree: a move maker running on a move
    // scope changes the solution in the global scope, so the operator's scope and its ancestors are checked
    for (var current = scope; current != null; current = current.Parent) Check(current, operation.Operator.Name);
  }

  private void Check(IScope scope, string by) {
    if (!scope.Variables.TryGetValue(solutionName!, out var variable) || variable.Value is not IItem value) return;
    long? fingerprint = Fingerprint(value);
    states.TryGetValue(scope, out var state);
    bool unchanged = state != null && ReferenceEquals(state.Value, value) && (fingerprint == null || state.Fingerprint == fingerprint);
    if (!unchanged) {
      int? known = state != null ? null : Known(value, fingerprint);
      int id;
      if (known is int existing) id = existing;  // a copy (selection), not a new solution
      else {
        // crossover: the parents are sub-scopes, usually copies made by selection: known by content
        var parents = scope.SubScopes.Select(Identify).Where(i => i >= 0).ToList();
        if (parents.Count == 0 && state != null) parents.Add(state.Id);
        if (pendingStart is double start) { trace.BeginIteration(++iteration, start); pendingStart = null; }
        var copy = trace.StoresCopies ? (IItem)value.Clone() : null;
        id = trace.Add(iteration, by, copy, parents, clock.Elapsed.TotalSeconds).Id;
        if (fingerprint is long f) byContent[f] = id;
        else byReference.AddOrUpdate(value, new StrongBox<int>(id));
      }
      if (state == null) states.Add(scope, state = new ScopeState());
      state.Value = value; state.Fingerprint = fingerprint; state.Id = id;
    }
    if (scope.Variables.TryGetValue(qualityName, out var q)) {
      double? quality = q.Value switch { DoubleValue d => d.Value, DoubleArray { Length: > 0 } a => a[0], _ => null };
      if (quality is double value2 && trace.Solution(state!.Id).Quality != value2) trace.SetQuality(state.Id, value2);
    }
  }

  private int Identify(IScope scope) {
    if (states.TryGetValue(scope, out var state)) return state.Id;
    return scope.Variables.TryGetValue(solutionName!, out var v) && v.Value is { } value ? Known(value, Fingerprint(value)) ?? -1 : -1;
  }

  private int? Known(object value, long? fingerprint) =>
    fingerprint is long f ? (byContent.TryGetValue(f, out var id) ? id : null)
                          : byReference.TryGetValue(value, out var box) ? box.Value : null;

  private void UpdateIteration() {
    foreach (var name in CounterNames)
      if (global!.Variables.TryGetValue(name, out var v) && v.Value is IntValue counter) {
        if (counter.Value != iteration) trace.BeginIteration(iteration = counter.Value, clock.Elapsed.TotalSeconds);
        return;
      }
  }

  /// <summary>Content hash for arrays/vectors/permutations, trees and VRP tours; null: compare by reference.</summary>
  internal static long? Fingerprint(object value) {
    unchecked {
      long hash = 1469598103934665603;
      void Mix(int h) { hash = (hash ^ h) * 1099511628211; }
      switch (value) {
        case ISymbolicExpressionTree tree:
          foreach (var node in tree.IterateNodesPrefix()) { Mix(node.SubtreeCount); Mix((node.ToString() ?? "").GetHashCode()); }
          return hash;
        case IVRPEncoding vrp:
          foreach (var tour in vrp.GetTours()) { Mix(-1); foreach (var stop in tour.Stops) Mix(stop); }
          return hash;
        case IStringConvertibleArray and IEnumerable sequence:
          foreach (var element in sequence) Mix(element?.GetHashCode() ?? 0);
          return hash;
        default:
          return null;
      }
    }
  }
}
