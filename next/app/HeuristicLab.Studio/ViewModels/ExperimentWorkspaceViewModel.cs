using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeuristicLab.Common;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Optimization;
using HeuristicLab.Scripting;
using HeuristicLab.Studio.Services;

namespace HeuristicLab.Studio.ViewModels;

/// <summary>
/// Experiments tab: several experiments side by side, each a tree of building blocks (algorithm +
/// problem, batch run, time-limit run) that is a plain HeuristicLab Experiment underneath.
/// Add buttons belong to their container; start buttons are enabled only when every experiment
/// they would start is complete (each algorithm has a problem, no container is empty).
/// </summary>
public partial class ExperimentWorkspaceViewModel : ViewModelBase {
  private readonly IDialogService? dialogs;
  private readonly IFileDialogService? fileDialogs;
  private readonly Func<string?> resultsFolder;
  private readonly Func<bool> busyElsewhere;
  private readonly Action<IOptimizer, string?> showInRunTab;
  private readonly Action<CSharpScript> openScript;
  private CancellationTokenSource? stopSource;
  private int experimentCounter, batchCounter, timeLimitCounter;

  public ExperimentWorkspaceViewModel(IDialogService? dialogs = null, IFileDialogService? fileDialogs = null,
      Func<string?>? resultsFolder = null, Func<bool>? busyElsewhere = null, Action<IOptimizer, string?>? showInRunTab = null,
      Action<CSharpScript>? openScript = null) {
    this.dialogs = dialogs;
    this.fileDialogs = fileDialogs;
    this.resultsFolder = resultsFolder ?? (() => null);
    this.busyElsewhere = busyElsewhere ?? (() => false);
    this.showInRunTab = showInRunTab ?? ((_, _) => { });
    this.openScript = openScript ?? (_ => { });
    Samples = new SamplesViewModel(OpenSampleAsync);
    Detail = Samples;
    _ = Samples.LoadAsync();
    liveTimer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(500), Avalonia.Threading.DispatcherPriority.Background, (_, _) => RefreshLive());
  }

  public ObservableCollection<ExperimentBlockViewModel> Experiments { get; } = [];

  public bool HasExperiments => Experiments.Count > 0;

  [ObservableProperty]
  public partial BlockViewModel? Selected { get; set; }

  partial void OnSelectedChanged(BlockViewModel? oldValue, BlockViewModel? newValue) {
    if (oldValue != null) { oldValue.IsSelected = false; oldValue.IsEditingName = false; }
    if (newValue != null) newValue.IsSelected = true;
    Detail = newValue switch {
      AlgorithmBlockViewModel a => a.ShowDetail(AlgorithmDetailViewModel.AlgorithmTab),
      ProblemBlockViewModel p => p.Owner.ShowDetail(AlgorithmDetailViewModel.ProblemTab),
      null => Samples,
      _ => newValue
    };
    if (newValue == null) _ = Samples.LoadAsync();
  }

  /// <summary>
  /// What the right-hand side shows: an algorithm's detail (tabs), a container's settings, or - with
  /// nothing selected - the samples (HeuristicLab's start page).
  /// </summary>
  [ObservableProperty]
  public partial ViewModelBase? Detail { get; set; }

  /// <summary>Experiments or a single algorithm are running (or paused): the tree cannot change.</summary>
  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(StartAllCommand), nameof(StartSelectedCommand), nameof(StopCommand))]
  [NotifyPropertyChangedFor(nameof(IsEditable))]
  public partial bool IsRunning { get; set; }

  /// <summary>Experiments started with the start buttons are running.</summary>
  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(StopCommand))]
  public partial bool IsRunningExperiments { get; set; }

  private bool algorithmRunning;

  /// <summary>An algorithm started from its detail view runs or is paused.</summary>
  public void SetAlgorithmRunning(bool running) {
    algorithmRunning = running;
    IsRunning = IsRunningExperiments || algorithmRunning;
  }

  partial void OnIsRunningChanged(bool value) {
    if (value) liveTimer.Start();
    RefreshLive();
    if (!value) liveTimer.Stop();
  }

  private readonly Avalonia.Threading.DispatcherTimer liveTimer;

  /// <summary>Whether live monitoring is active (anything is running).</summary>
  public bool IsLive => liveTimer.IsEnabled;

  /// <summary>
  /// Live monitoring while anything runs: the shown detail (results, selected result's
  /// visualization, runs), the experiments' run tables and the tree's counters. Called by a timer
  /// every half second; reads are tolerant of the running algorithm changing the data.
  /// </summary>
  public void RefreshLive() {
    switch (Detail) {
      case AlgorithmDetailViewModel a: a.Refresh(); break;
      case ContainerBlockViewModel c: c.RefreshRuns(); break;
    }
    foreach (var a in AllBlocks(Experiments).OfType<AlgorithmBlockViewModel>()) a.RefreshCounts();
  }

  /// <summary>Building blocks cannot change while experiments run.</summary>
  public bool IsEditable => !IsRunning;

  [ObservableProperty]
  public partial string Status { get; set; } = "";

  // ---- creating and opening experiments

  /// <summary>HeuristicLab's start page samples.</summary>
  public SamplesViewModel Samples { get; }

  [RelayCommand]
  private void ShowSamples() {
    Selected = null;
    Detail = Samples;
    _ = Samples.LoadAsync();
  }

  /// <summary>An algorithm sample becomes an experiment; a script sample opens in the Scripts tab.</summary>
  public async Task OpenSampleAsync(SampleInfo sample) {
    try {
      Status = $"Opening {sample.Name} ...";
      var content = await Task.Run(() => HeuristicLab.Next.Runtime.Samples.Load(sample.Id));
      Open(content, sample.Name, null);
      if (sample.Limitation != null) Status += $" Note: {sample.Limitation}";
    } catch (Exception e) {
      Status = $"Could not open {sample.Name}: {e.Message}";
    }
  }

  [RelayCommand]
  private void CreateExperiment() => NewExperiment();

  public ExperimentBlockViewModel NewExperiment() {
    var experiment = new Experiment { Name = $"New experiment {++experimentCounter}" };
    return AddExperiment(experiment, null);
  }

  [RelayCommand]
  private async Task OpenExperimentAsync() {
    if (fileDialogs != null && await fileDialogs.OpenHlFileAsync() is string path) await OpenExperimentAsync(path);
  }

  /// <summary>Opens a .hl file: an experiment as it is, any other optimizer inside a new experiment.</summary>
  public async Task<ExperimentBlockViewModel?> OpenExperimentAsync(string path) {
    try {
      var content = await Task.Run(() => Documents.Load(path));
      return Open(content, Path.GetFileName(path), path);
    } catch (Exception e) {
      Status = $"Could not open {Path.GetFileName(path)}: {e.Message}";
      return null;
    }
  }

  /// <summary>An experiment as it is, any other optimizer inside a new experiment, a script in the Scripts tab.</summary>
  private ExperimentBlockViewModel? Open(object content, string name, string? path) {
    switch (content) {
      case Experiment experiment:
        Status = $"Opened {name}";
        return AddExperiment(experiment, path);
      case IOptimizer optimizer:
        var wrapper = new Experiment { Name = path != null ? Path.GetFileNameWithoutExtension(path) : name };
        wrapper.Optimizers.Add(optimizer);
        Status = $"Opened {name} ({optimizer.ItemName}) as a new experiment.";
        return AddExperiment(wrapper, null);
      case CSharpScript script:
        openScript(script);
        Status = $"Opened {name} in the Scripts tab.";
        return null;
      default:
        Status = $"{name} does not contain an experiment, batch run, algorithm or script.";
        return null;
    }
  }

  private ExperimentBlockViewModel AddExperiment(Experiment experiment, string? path) {
    var block = new ExperimentBlockViewModel(this, experiment, path);
    Experiments.Add(block);
    Selected = block;
    OnPropertyChanged(nameof(HasExperiments));
    RefreshState();
    return block;
  }

  internal async Task SaveAsync(ExperimentBlockViewModel block) {
    if (fileDialogs == null) return;
    var path = await fileDialogs.SaveHlFileAsync(Path.GetFileName(block.FilePath) ?? block.Name + ".hl");
    if (path == null) return;
    await Task.Run(() => Documents.Save(block.Experiment, path));
    block.FilePath = path;
    Status = $"Saved {block.Name} to {Path.GetFileName(path)}";
  }

  internal void ShowInRunTab(BlockViewModel block) {
    var optimizer = block switch {
      ExperimentBlockViewModel e => (IOptimizer)e.Experiment,
      ContainerBlockViewModel c => c.Optimizer,
      AlgorithmBlockViewModel a => a.Algorithm,
      _ => null
    };
    if (optimizer != null) showInRunTab(optimizer, (block as ExperimentBlockViewModel)?.FilePath);
  }

  // ---- building blocks

  internal BlockViewModel CreateBlock(IOptimizer optimizer, ContainerBlockViewModel parent) =>
    optimizer is IAlgorithm algorithm && !ExperimentTree.IsContainer(optimizer)
      ? new AlgorithmBlockViewModel(this, parent, algorithm)
      : new ContainerBlockViewModel(this, parent, optimizer);

  internal async Task AddAlgorithmAsync(ContainerBlockViewModel container) {
    if (dialogs != null && await dialogs.PickAlgorithmAsync() is CatalogEntry entry) AddAlgorithm(container, entry);
  }

  /// <summary>Adds a new algorithm (without problem) at the end of this container.</summary>
  public AlgorithmBlockViewModel AddAlgorithm(ContainerBlockViewModel container, CatalogEntry entry) {
    var algorithm = (IAlgorithm)entry.CreateInstance();
    ExperimentTree.Add(container.Optimizer, algorithm);
    var block = new AlgorithmBlockViewModel(this, container, algorithm);
    AddChild(container, block);
    return block;
  }

  /// <summary>Adds a new batch run or time-limit run at the end of this container.</summary>
  public ContainerBlockViewModel AddContainer(ContainerBlockViewModel container, CatalogEntry entry) {
    var optimizer = (IOptimizer)entry.CreateInstance();
    optimizer.Name = optimizer switch {
      BatchRun => $"Batch run {++batchCounter}",
      TimeLimitRun => $"Time-limit run {++timeLimitCounter}",
      _ => optimizer.Name
    };
    ExperimentTree.Add(container.Optimizer, optimizer);
    var block = new ContainerBlockViewModel(this, container, optimizer);
    AddChild(container, block);
    return block;
  }

  private void AddChild(ContainerBlockViewModel container, BlockViewModel block) {
    container.Children.Add(block);
    container.ChildrenChanged();
    Selected = block;
    RefreshState();
  }

  internal async Task ChooseProblemAsync(AlgorithmBlockViewModel block) {
    if (dialogs != null && await dialogs.PickProblemAsync(block.Algorithm) is IProblem problem) SetProblem(block, problem);
  }

  public void SetProblem(AlgorithmBlockViewModel block, IProblem? problem) {
    if (IsRunning) return;
    block.SetProblem(problem);
    if (block.Problem != null) Selected = block.Problem;
    RefreshState();
  }

  /// <summary>
  /// Deletes a block. Containers go with all their children, after a confirmation when they have
  /// any; a problem block only clears its algorithm's problem; an experiment leaves the workspace
  /// (its file stays on disk).
  /// </summary>
  public async Task DeleteAsync(BlockViewModel block) {
    if (IsRunning) return;
    if (block is ContainerBlockViewModel { Children.Count: > 0 } container && dialogs != null) {
      var what = block is ExperimentBlockViewModel ? "Remove the experiment" : $"Delete {block.Name}";
      if (!await dialogs.ConfirmAsync("Delete", $"{what} and its {container.Children.Count} building block(s)?")) return;
    }
    if (Selected != null && IsWithin(Selected, block)) Selected = null;
    switch (block) {
      case ProblemBlockViewModel problem:
        problem.Owner.SetProblem(null);
        break;
      case ExperimentBlockViewModel experiment:
        Experiments.Remove(experiment);
        OnPropertyChanged(nameof(HasExperiments));
        break;
      default:
        var parent = block.Parent!;
        ExperimentTree.Remove(parent.Optimizer, (IOptimizer)block.Item);
        parent.Children.Remove(block);
        parent.ChildrenChanged();
        break;
    }
    RefreshState();
  }

  private static bool IsWithin(BlockViewModel candidate, BlockViewModel ancestor) {
    for (BlockViewModel? b = candidate is ProblemBlockViewModel p ? p.Owner : candidate; b != null; b = b.Parent)
      if (b == ancestor || candidate == ancestor) return true;
    return false;
  }

  // ---- starting

  /// <summary>Recomputes what is missing per experiment and the start buttons' state.</summary>
  public void RefreshState() {
    foreach (var e in Experiments) e.Missing = string.Join(" ", ExperimentTree.Problems(e.Experiment));
    StartAllCommand.NotifyCanExecuteChanged();
    StartSelectedCommand.NotifyCanExecuteChanged();
  }

  [RelayCommand(CanExecute = nameof(CanStartAll))]
  private Task StartAllAsync() => StartAsync(Experiments.ToList());
  private bool CanStartAll() => CanStart(Experiments.ToList());

  [RelayCommand(CanExecute = nameof(CanStartSelected))]
  private Task StartSelectedAsync() => StartAsync(Experiments.Where(e => e.IsChecked).ToList());
  private bool CanStartSelected() => CanStart(Experiments.Where(e => e.IsChecked).ToList());

  private bool CanStart(System.Collections.Generic.IReadOnlyList<ExperimentBlockViewModel> experiments) =>
    !IsRunning && !busyElsewhere() && experiments.Count > 0 && experiments.All(e => e.IsReady);

  [RelayCommand(CanExecute = nameof(IsRunningExperiments))]
  private void Stop() => stopSource?.Cancel();

  /// <summary>
  /// Runs the experiments at the same time, each through OptimizerRunner. No seed is imposed:
  /// a fixed seed would make every repetition of a batch run identical, so each algorithm keeps
  /// its own seed setting (random by default).
  /// </summary>
  private async Task StartAsync(System.Collections.Generic.IReadOnlyList<ExperimentBlockViewModel> experiments) {
    IsRunningExperiments = true;
    IsRunning = true;
    stopSource = new CancellationTokenSource();
    var folder = resultsFolder();
    var store = folder == null ? null : new ResultStore(folder);
    Status = experiments.Count == 1 ? $"Running {experiments[0].Name} ..." : $"Running {experiments.Count} experiments ...";
    try {
      // the Resources tab may limit how many experiments run at the same time
      int concurrent = Resources.Current.ConcurrentExperiments;
      using var slots = new SemaphoreSlim(concurrent > 0 ? concurrent : Math.Max(1, experiments.Count));
      var token = stopSource.Token;
      var started = await Task.WhenAll(experiments.Select(async e => {
        e.RunStatus = "Waiting for a free slot ...";
        try {
          await slots.WaitAsync(token);
        } catch (OperationCanceledException) {
          e.RunStatus = "Not started (stopped)";
          return null;
        }
        try {
          Resources.Configure(e.Experiment);
          e.RunStatus = "Running ...";
          var progress = new Progress<RunProgress>(p => {
            if (e.RunStatus.StartsWith("Running", StringComparison.Ordinal)) e.RunStatus = $"Running ... {p.Elapsed:hh\\:mm\\:ss}";
          });
          var report = await OptimizerRunner.RunAsync(e.Experiment,
            new RunOptions { ProgressInterval = TimeSpan.FromMilliseconds(500), Labels = new System.Collections.Generic.Dictionary<string, string> { ["experiment"] = e.Name } },
            e.FilePath, progress, token);
          store?.Add(report);
          e.RunStatus = report.Outcome switch {
            RunOutcome.Completed => $"Completed: {report.Runs.Count} runs in {TimeSpan.FromSeconds(report.ExecutionSeconds):hh\\:mm\\:ss}",
            RunOutcome.Stopped => $"Stopped: {report.Runs.Count} runs",
            _ => $"Failed: {report.Error?.Split('\n')[0]}"
          };
          Resources.Tag(Resources.RunsOf(e.Experiment));
          return report;
        } finally {
          slots.Release();
        }
      }));
      var reports = started.OfType<RunReport>().ToArray();
      Status = $"{reports.Count(r => r.Outcome == RunOutcome.Completed)} of {reports.Length} experiments completed, " +
               $"{reports.Sum(r => r.Runs.Count)} runs" + (store != null ? $", stored in {store.Directory}" : "");
    } finally {
      IsRunningExperiments = false;
      IsRunning = algorithmRunning;
      RefreshLive();
      stopSource.Dispose();
      stopSource = null;
      RefreshState();
    }
  }

  private static System.Collections.Generic.IEnumerable<BlockViewModel> AllBlocks(System.Collections.Generic.IEnumerable<BlockViewModel> blocks) =>
    blocks.SelectMany(b => b is ContainerBlockViewModel c ? AllBlocks(c.Children).Prepend(b) : [b]);

  // ---- formatting

  public static string DisplayName(CatalogEntry entry) =>
    entry.Type == typeof(BatchRun) ? "Batch run" : entry.Type == typeof(TimeLimitRun) ? "Time-limit run" : entry.Name;

  public static string FormatDuration(TimeSpan t) =>
    t.TotalSeconds < 60 && t.TotalSeconds == Math.Floor(t.TotalSeconds) ? $"{t.TotalSeconds:0} s"
    : t.TotalMinutes < 60 && t.Seconds == 0 && t.Milliseconds == 0 ? $"{t.TotalMinutes:0} min"
    : t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

  /// <summary>"90", "90s", "5m", "5 min", "1h" or "hh:mm:ss"; null if not a duration.</summary>
  public static TimeSpan? ParseDuration(string text) {
    text = text.Trim();
    var m = Regex.Match(text, @"^(\d+(?:\.\d+)?)\s*(s|sec|m|min|h)?$", RegexOptions.IgnoreCase);
    if (m.Success) {
      var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
      return m.Groups[2].Value.ToLowerInvariant() switch {
        "m" or "min" => TimeSpan.FromMinutes(value),
        "h" => TimeSpan.FromHours(value),
        _ => TimeSpan.FromSeconds(value)
      };
    }
    return TimeSpan.TryParseExact(text, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var span) ? span : null;
  }
}
