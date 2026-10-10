using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Studio.Controls;

namespace HeuristicLab.Studio.ViewModels;

/// <summary>
/// "Resources" tab: what the program may use - CPU cores, threads per algorithm, experiments at the
/// same time, memory, priority - applied at once and remembered; and what it uses right now. Runs
/// record the limits they were made with ("Resources: ..." parameters), so the Results tab can
/// group by them and show what the limits change.
/// </summary>
public partial class ResourcesViewModel : ViewModelBase {
  private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
  private readonly Action<ResourceLimits> save;
  private readonly Action<string> stopEverything;
  private readonly ResourceMonitor monitor = new();
  private readonly DispatcherTimer refresh;
  private bool loading;

  public ResourcesViewModel(ResourceLimits initial, Action<ResourceLimits> save, Action<string> stopEverything) {
    this.save = save;
    this.stopEverything = stopEverything;
    Show(initial.Clamped());
    Apply();
    monitor.MemoryExceeded += sample => Dispatcher.UIThread.Post(() => OnMemoryExceeded(sample));
    refresh = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshUsage());
    refresh.Start();
  }

  public int LogicalProcessors => Resources.LogicalProcessors;
  public string MachineText => $"{Resources.LogicalProcessors} logical processors · {Resources.TotalMemoryGB.ToString("0.#", Invariant)} GB memory";
  public string Gpu => Resources.Gpu;
  public IReadOnlyList<ResourcePriority> Priorities { get; } = Enum.GetValues<ResourcePriority>();

  [ObservableProperty]
  public partial decimal Cores { get; set; }

  [ObservableProperty]
  public partial decimal ThreadsPerAlgorithm { get; set; }

  /// <summary>0: all started experiments at once.</summary>
  [ObservableProperty]
  public partial decimal ConcurrentExperiments { get; set; }

  /// <summary>0: no limit.</summary>
  [ObservableProperty]
  public partial decimal MemoryLimitGB { get; set; }

  [ObservableProperty]
  public partial ResourcePriority Priority { get; set; }

  [ObservableProperty]
  public partial string Applied { get; set; } = "";

  [ObservableProperty]
  public partial string Problems { get; set; } = "";

  [ObservableProperty]
  public partial string Usage { get; set; } = "";

  [ObservableProperty]
  public partial IReadOnlyList<ChartSeries> UsageSeries { get; set; } = [];

  partial void OnCoresChanged(decimal value) => Apply();
  partial void OnThreadsPerAlgorithmChanged(decimal value) => Apply();
  partial void OnConcurrentExperimentsChanged(decimal value) => Apply();
  partial void OnMemoryLimitGBChanged(decimal value) => Apply();
  partial void OnPriorityChanged(ResourcePriority value) => Apply();

  public ResourceLimits Limits => new ResourceLimits {
    Cores = (int)Cores, ThreadsPerAlgorithm = (int)ThreadsPerAlgorithm, ConcurrentExperiments = (int)ConcurrentExperiments,
    MemoryLimitGB = (double)MemoryLimitGB, Priority = Priority
  }.Clamped();

  private void Show(ResourceLimits limits) {
    loading = true;
    Cores = limits.Cores;
    ThreadsPerAlgorithm = limits.ThreadsPerAlgorithm;
    ConcurrentExperiments = limits.ConcurrentExperiments;
    MemoryLimitGB = (decimal)limits.MemoryLimitGB;
    Priority = limits.Priority;
    loading = false;
  }

  private void Apply() {
    if (loading) return;
    var limits = Limits;
    var problems = Resources.Apply(limits);
    Problems = string.Join(Environment.NewLine, problems);
    Applied = "Now: " + Resources.Describe(limits) + $", priority {limits.Priority}.";
    save(limits);
  }

  /// <summary>HeuristicLab's defaults: every core, one thread per algorithm (sequential engine), no limits.</summary>
  [RelayCommand]
  private void Defaults() {
    Show(new ResourceLimits().Clamped());
    Apply();
  }

  private void OnMemoryExceeded(ResourceSample sample) {
    stopEverything($"Stopped: the program used {sample.WorkingSetMB / 1024:0.0} GB, more than the memory limit of {MemoryLimitGB} GB (Resources tab).");
  }

  public void RefreshUsage() {
    var samples = monitor.Samples;
    if (samples.Count == 0) return;
    var last = samples[^1];
    Usage = $"CPU {last.CpuPercent:0}% of all processors · memory {last.WorkingSetMB:0} MB (managed {last.ManagedHeapMB:0} MB) · {last.Threads} threads";
    double start = samples[0].Seconds;
    UsageSeries = [
      new ChartSeries("CPU [%]", ChartSeries.PaletteColor(0), samples.Select(s => new ChartPoint(s.Seconds - start, s.CpuPercent)).ToList()),
      new ChartSeries("Memory [MB]", ChartSeries.PaletteColor(1), samples.Select(s => new ChartPoint(s.Seconds - start, s.WorkingSetMB)).ToList(), SecondYAxis: true)
    ];
  }
}
