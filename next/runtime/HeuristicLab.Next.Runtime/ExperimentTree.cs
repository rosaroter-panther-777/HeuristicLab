using HeuristicLab.Optimization;

namespace HeuristicLab.Next.Runtime;

/// <summary>
/// Building blocks of an experiment as a tree over HeuristicLab's own optimizers, so that an
/// experiment built here is a plain HeuristicLab Experiment (.hl) and runs with OptimizerRunner.
/// <list type="bullet">
/// <item>Experiment: any number of children (algorithms, batch runs, time-limit runs, experiments).</item>
/// <item>Batch run: repeats its content Repetitions times. HeuristicLab's BatchRun repeats one
/// optimizer, so several children live in an inner Experiment; each run repeats all of them.</item>
/// <item>Time-limit run: exactly one algorithm, stopped after MaximumExecutionTime.</item>
/// </list>
/// </summary>
public static class ExperimentTree {
  /// <summary>Name of the inner experiment holding a batch run's children.</summary>
  public const string BatchContentName = "Batch content";

  public static bool IsContainer(IOptimizer optimizer) => optimizer is Experiment or BatchRun or TimeLimitRun;

  /// <summary>Containers that can be added as building blocks, as discovered from the plugins.</summary>
  public static IReadOnlyList<CatalogEntry> ContainerTypes() =>
    Catalog.MetaOptimizers().Where(e => e.Type == typeof(BatchRun) || e.Type == typeof(TimeLimitRun) || e.Type == typeof(Experiment)).ToList();

  public static IReadOnlyList<IOptimizer> Children(IOptimizer container) => container switch {
    Experiment e => e.Optimizers.ToList(),
    BatchRun { Optimizer: Experiment inner } when inner.Name == BatchContentName => inner.Optimizers.ToList(),
    BatchRun { Optimizer: IOptimizer single } => [single],
    TimeLimitRun { Algorithm: IAlgorithm algorithm } => [algorithm],
    _ => []
  };

  /// <summary>Whether a child of this type can be added (a time-limit run takes one algorithm only).</summary>
  public static bool CanAdd(IOptimizer container, Type childType) => container switch {
    Experiment or BatchRun => typeof(IOptimizer).IsAssignableFrom(childType),
    TimeLimitRun t => t.Algorithm == null && typeof(IAlgorithm).IsAssignableFrom(childType),
    _ => false
  };

  public static void Add(IOptimizer container, IOptimizer child) {
    if (!CanAdd(container, child.GetType()))
      throw new ArgumentException($"{container.Name} cannot contain {child.Name}.");
    switch (container) {
      case Experiment e:
        e.Optimizers.Add(child);
        break;
      case BatchRun b:
        if (b.Optimizer is not Experiment { Name: BatchContentName } inner) {
          inner = new Experiment { Name = BatchContentName };
          var existing = b.Optimizer;
          b.Optimizer = null;
          if (existing != null) inner.Optimizers.Add(existing);
          b.Optimizer = inner;
        }
        inner.Optimizers.Add(child);
        break;
      case TimeLimitRun t:
        t.Algorithm = (IAlgorithm)child;
        break;
    }
  }

  public static void Remove(IOptimizer container, IOptimizer child) {
    switch (container) {
      case Experiment e: e.Optimizers.Remove(child); break;
      case BatchRun { Optimizer: Experiment { Name: BatchContentName } inner }: inner.Optimizers.Remove(child); break;
      case BatchRun b when b.Optimizer == child: b.Optimizer = null; break;
      case TimeLimitRun t when t.Algorithm == child: t.Algorithm = null; break;
    }
  }

  public static IEnumerable<IAlgorithm> Algorithms(IOptimizer root) =>
    IsContainer(root) ? Children(root).SelectMany(Algorithms) : root is IAlgorithm a ? [a] : [];

  /// <summary>Why the tree cannot run yet; empty when it is ready to start.</summary>
  public static IReadOnlyList<string> Problems(IOptimizer root) {
    var problems = new List<string>();
    void Visit(IOptimizer node) {
      if (node is IAlgorithm algorithm) {
        if (algorithm.Problem == null) problems.Add($"{algorithm.Name} has no problem.");
      } else if (IsContainer(node)) {
        var children = Children(node);
        if (children.Count == 0) problems.Add($"{node.Name} is empty.");
        foreach (var child in children) Visit(child);
      }
    }
    Visit(root);
    return problems;
  }
}
