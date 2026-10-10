using System.Diagnostics;
using System.Globalization;
using HeuristicLab.Core;
using HeuristicLab.Data;
using HeuristicLab.Optimization;

namespace HeuristicLab.Next.Runtime;

public enum ResourcePriority { Normal, BelowNormal, Lowest }

/// <summary>
/// What the program may use. Cores: the logical processors the whole process may run on (a hard
/// limit, set as processor affinity). ThreadsPerAlgorithm: 1 runs algorithms on HeuristicLab's
/// sequential engine (its default), more on its parallel engine with that many threads (only
/// operators that process sub-scopes in parallel, mostly evaluating a population, gain).
/// ConcurrentExperiments: how many started experiments run at the same time (0: all).
/// MemoryLimitGB: when the process uses more, running work is stopped (0: no limit).
/// HeuristicLab runs nothing on the GPU, so there is no graphics memory to limit.
/// </summary>
public sealed record ResourceLimits {
  public int Cores { get; init; } = Environment.ProcessorCount;
  public int ThreadsPerAlgorithm { get; init; } = 1;
  public int ConcurrentExperiments { get; init; }
  public double MemoryLimitGB { get; init; }
  public ResourcePriority Priority { get; init; } = ResourcePriority.Normal;

  /// <summary>Within what this machine has.</summary>
  public ResourceLimits Clamped() => this with {
    Cores = Math.Clamp(Cores, 1, Environment.ProcessorCount),
    ThreadsPerAlgorithm = Math.Clamp(ThreadsPerAlgorithm, 1, Environment.ProcessorCount),
    ConcurrentExperiments = Math.Max(0, ConcurrentExperiments),
    MemoryLimitGB = Math.Max(0, MemoryLimitGB)
  };
}

public sealed record ResourceSample(double Seconds, double CpuPercent, double WorkingSetMB, double ManagedHeapMB, int Threads);

/// <summary>Applies resource limits to the process and to algorithms, records them in runs, and watches usage.</summary>
public static class Resources {
  public const string CoresParameter = "Resources: CPU cores", ThreadsParameter = "Resources: threads per algorithm",
                      MemoryParameter = "Resources: memory limit [GB]";

  private static readonly object Sync = new();
  public static ResourceLimits Current { get; private set; } = new();

  public static int LogicalProcessors => Environment.ProcessorCount;

  /// <summary>Memory the runtime sees (physical memory, or a container's limit).</summary>
  public static double TotalMemoryGB => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024;

  public static string Gpu => "Not used: no HeuristicLab algorithm or problem runs on the graphics card.";

  /// <summary>Applies the limits to this process; returns what could not be applied (e.g. affinity on macOS).</summary>
  public static IReadOnlyList<string> Apply(ResourceLimits limits) {
    limits = limits.Clamped();
    var problems = new List<string>();
    var process = Process.GetCurrentProcess();
    if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) {
      try {
        long mask = limits.Cores >= 63 ? -1 : (1L << limits.Cores) - 1;
        process.ProcessorAffinity = (nint)mask;
      } catch (Exception e) when (e is System.ComponentModel.Win32Exception or PlatformNotSupportedException or InvalidOperationException) {
        problems.Add($"CPU cores could not be limited: {e.Message}");
      }
    } else if (limits.Cores < LogicalProcessors) problems.Add("Limiting CPU cores is not supported on this system.");
    try {
      process.PriorityClass = limits.Priority switch {
        ResourcePriority.BelowNormal => ProcessPriorityClass.BelowNormal,
        ResourcePriority.Lowest => ProcessPriorityClass.Idle,
        _ => ProcessPriorityClass.Normal
      };
    } catch (Exception e) when (e is System.ComponentModel.Win32Exception or PlatformNotSupportedException or UnauthorizedAccessException) {
      // raising the priority again needs rights on Linux (nice); lowering always works
      problems.Add($"Priority could not be set to {limits.Priority}: {e.Message}");
    }
    lock (Sync) Current = limits;
    return problems;
  }

  /// <summary>
  /// Before an algorithm starts: with more than one thread per algorithm, HeuristicLab's parallel
  /// engine with that many threads; with one, the sequential engine. A debug engine (chosen for
  /// breakpoints) is kept. Only prepared algorithms are changed (changing the engine prepares).
  /// </summary>
  public static void Configure(IOptimizer optimizer) {
    int threads = Current.ThreadsPerAlgorithm;
    foreach (var algorithm in ExperimentTree.Algorithms(optimizer).OfType<EngineAlgorithm>()) {
      if (algorithm.ExecutionState != ExecutionState.Prepared) continue;
      switch (algorithm.Engine) {
        case HeuristicLab.ParallelEngine.ParallelEngine parallel when threads > 1:
          parallel.DegreeOfParallelism = threads;
          break;
        case HeuristicLab.ParallelEngine.ParallelEngine when threads == 1:
          algorithm.Engine = new HeuristicLab.SequentialEngine.SequentialEngine();
          break;
        case HeuristicLab.SequentialEngine.SequentialEngine or null when threads > 1:
          algorithm.Engine = new HeuristicLab.ParallelEngine.ParallelEngine { DegreeOfParallelism = threads };
          break;
      }
    }
  }

  /// <summary>Records the limits a run was made with as its parameters (once), so runs can be compared by them.</summary>
  public static void Tag(IEnumerable<IRun> runs) {
    var limits = Current;
    foreach (var run in runs) {
      if (run.Parameters.ContainsKey(CoresParameter)) continue;
      run.Parameters.Add(CoresParameter, new IntValue(limits.Cores));
      run.Parameters.Add(ThreadsParameter, new IntValue(limits.ThreadsPerAlgorithm));
      run.Parameters.Add(MemoryParameter, new DoubleValue(limits.MemoryLimitGB));
    }
  }

  /// <summary>Every run of an optimizer and the optimizers inside it.</summary>
  public static IEnumerable<IRun> RunsOf(IOptimizer optimizer) =>
    optimizer.Runs.Concat(ExperimentTree.Children(optimizer).SelectMany(RunsOf));

  public static string Describe(ResourceLimits limits) =>
    $"{limits.Cores} of {LogicalProcessors} cores, {limits.ThreadsPerAlgorithm} thread{(limits.ThreadsPerAlgorithm == 1 ? "" : "s")} per algorithm, " +
    (limits.ConcurrentExperiments == 0 ? "all experiments at once, " : $"{limits.ConcurrentExperiments} experiment{(limits.ConcurrentExperiments == 1 ? "" : "s")} at once, ") +
    (limits.MemoryLimitGB > 0 ? $"memory up to {limits.MemoryLimitGB.ToString("0.#", CultureInfo.InvariantCulture)} GB" : "no memory limit");
}

/// <summary>
/// Samples the process once a second: CPU use (100 % = all logical processors busy), working set,
/// managed heap and threads, keeping the last few minutes. Raises MemoryExceeded when the working
/// set stays above the memory limit after a full garbage collection.
/// </summary>
public sealed class ResourceMonitor : IDisposable {
  private readonly Timer timer;
  private readonly Stopwatch clock = Stopwatch.StartNew();
  private readonly Process process = Process.GetCurrentProcess();
  private readonly List<ResourceSample> samples = [];
  private TimeSpan lastCpu;
  private double lastSeconds;

  public ResourceMonitor(TimeSpan? interval = null, int keep = 180) {
    Keep = keep;
    lastCpu = process.TotalProcessorTime;
    timer = new Timer(_ => Sample(), null, TimeSpan.Zero, interval ?? TimeSpan.FromSeconds(1));
  }

  public int Keep { get; }
  public event Action<ResourceSample>? MemoryExceeded;

  public IReadOnlyList<ResourceSample> Samples { get { lock (samples) return samples.ToArray(); } }

  public ResourceSample Sample() {
    process.Refresh();
    double seconds = clock.Elapsed.TotalSeconds;
    var cpu = process.TotalProcessorTime;
    double elapsed = Math.Max(1e-3, seconds - lastSeconds);
    double percent = Math.Clamp((cpu - lastCpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100, 0, 100);
    lastCpu = cpu; lastSeconds = seconds;
    var sample = new ResourceSample(seconds, percent, process.WorkingSet64 / 1024.0 / 1024, GC.GetTotalMemory(false) / 1024.0 / 1024, process.Threads.Count);
    lock (samples) {
      samples.Add(sample);
      if (samples.Count > Keep) samples.RemoveAt(0);
    }
    double limitMB = Resources.Current.MemoryLimitGB * 1024;
    if (limitMB > 0 && sample.WorkingSetMB > limitMB) {
      GC.Collect();
      process.Refresh();
      if (process.WorkingSet64 / 1024.0 / 1024 > limitMB) MemoryExceeded?.Invoke(sample);
    }
    return sample;
  }

  public void Dispose() => timer.Dispose();
}
