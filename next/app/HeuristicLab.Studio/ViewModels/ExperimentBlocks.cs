using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeuristicLab.Core;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Optimization;

namespace HeuristicLab.Studio.ViewModels;

/// <summary>What an algorithm block's status square shows.</summary>
public enum AlgorithmStatus { NotRun, ProblemMissing, Waiting, Working, Finished, Failed }

/// <summary>Entry of an add menu or a block's context menu.</summary>
public sealed record MenuChoice(string Display, System.Windows.Input.ICommand Command);

/// <summary>
/// An existing building block (green in the mockups): experiment, batch run, time-limit run,
/// algorithm or problem. Each block wraps its own HeuristicLab object, so editing one never
/// touches another block of the same name.
/// </summary>
public abstract partial class BlockViewModel(ExperimentWorkspaceViewModel workspace, ContainerBlockViewModel? parent) : ViewModelBase {
  public ExperimentWorkspaceViewModel Workspace { get; } = workspace;
  public ContainerBlockViewModel? Parent { get; } = parent;
  public abstract INamedItem Item { get; }
  /// <summary>What kind of block this is, e.g. "Batch Run" or "Traveling Salesman Problem".</summary>
  public string Kind => Item.ItemName;

  public string Name {
    get => Item.Name;
    set {
      if (string.IsNullOrWhiteSpace(value) || value == Item.Name || !Item.CanChangeName) return;
      Item.Name = value.Trim();
      MarkModified();
      OnPropertyChanged();
      OnPropertyChanged(nameof(Label));
    }
  }

  /// <summary>Text on the block.</summary>
  public virtual string Label => Name;

  /// <summary>The top-level experiment this block belongs to.</summary>
  public ExperimentBlockViewModel? Root {
    get {
      BlockViewModel? b = this is ProblemBlockViewModel p ? p.Owner : this;
      while (b?.Parent != null) b = b.Parent;
      return b as ExperimentBlockViewModel;
    }
  }

  /// <summary>The experiment differs from its file now (shown as "unsaved").</summary>
  public void MarkModified() {
    if (Root is { } root) root.IsModified = true;
  }

  /// <summary>Pictogram of the block's kind (Assets/Pictograms).</summary>
  public abstract string Pictogram { get; }

  /// <summary>Second square: an experiment's saved state, an algorithm's status; none for other blocks.</summary>
  public virtual string? StatePictogram => null;
  public virtual string StateTip => "";
  public bool HasState => StatePictogram != null;

  /// <summary>Last field (batch runs and time-limit runs): repetitions / time limit, counting while running.</summary>
  public virtual bool HasBadge => false;
  public virtual string Badge => "";
  public virtual string BadgeTip => "";

  /// <summary>Algorithms and problems have names of one width, so problems line up.</summary>
  public virtual bool FixedWidth => false;

  [ObservableProperty]
  public partial bool IsEditingName { get; set; }

  [ObservableProperty]
  public partial bool IsSelected { get; set; }

  /// <summary>Parameters shown in the details panel (algorithms and problems).</summary>
  public IReadOnlyList<ParameterRowViewModel> Parameters =>
    Item is IParameterizedItem item
      ? ParameterEditor.Describe(item).Select(p => new ParameterRowViewModel(item, p, e => Workspace.Status = e)).ToList()
      : [];

  public bool HasParameters => Item is IParameterizedItem;

  [RelayCommand]
  private void Select() => Workspace.Selected = this;

  [RelayCommand]
  private void Rename() {
    Workspace.Selected = this;
    IsEditingName = true;
  }

  [RelayCommand]
  private Task DeleteAsync() => Workspace.DeleteAsync(this);

  /// <summary>Context menu of the block.</summary>
  public virtual IReadOnlyList<MenuChoice> Menu => [new("Rename", RenameCommand), new("Delete", DeleteCommand)];

  public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

/// <summary>Experiment, batch run or time-limit run: children plus its own add button.</summary>
public partial class ContainerBlockViewModel : BlockViewModel {
  public ContainerBlockViewModel(ExperimentWorkspaceViewModel workspace, ContainerBlockViewModel? parent, IOptimizer optimizer)
      : base(workspace, parent) {
    Optimizer = optimizer;
    foreach (var child in ExperimentTree.Children(optimizer)) Children.Add(workspace.CreateBlock(child, this));
    // offered building blocks: every algorithm (picked in a dialog) and the discovered containers
    var choices = new List<MenuChoice> { new("Algorithm...", new AsyncRelayCommand(() => Workspace.AddAlgorithmAsync(this))) };
    if (AcceptsContainers)
      foreach (var entry in ExperimentTree.ContainerTypes().Where(e => e.Type != typeof(Experiment)))
        choices.Add(new(ExperimentWorkspaceViewModel.DisplayName(entry), new RelayCommand(() => Workspace.AddContainer(this, entry))));
    AddChoices = choices;
  }

  public IOptimizer Optimizer { get; }
  public override INamedItem Item => Optimizer;

  private RunsViewModel? runs;
  /// <summary>Runs collected by this experiment, batch run or time-limit run.</summary>
  public RunsViewModel Runs {
    get {
      runs ??= new RunsViewModel(Optimizer.Runs);
      runs.Refresh();
      return runs;
    }
  }

  public void RefreshRuns() => runs?.Refresh();
  public ObservableCollection<BlockViewModel> Children { get; } = [];
  public IReadOnlyList<MenuChoice> AddChoices { get; }

  public override IReadOnlyList<MenuChoice> Menu =>
    [new("Show in Run tab", new RelayCommand(() => Workspace.ShowInRunTab(this))), .. base.Menu];

  /// <summary>A time-limit run holds one algorithm, so its add button goes away once it has one.</summary>
  public bool CanAdd => Optimizer is not TimeLimitRun || Children.Count == 0;

  /// <summary>Only algorithms go into a time-limit run.</summary>
  public bool AcceptsContainers => Optimizer is not TimeLimitRun;

  public string AddText => Optimizer is TimeLimitRun ? "+ Add algorithm" : "+ Add batch run / time-limit run / algorithm";

  public override string Pictogram => Optimizer switch {
    BatchRun => "batch_run",
    TimeLimitRun => "time_limit_run",
    _ => "experiment_logo"
  };

  /// <summary>
  /// Last field of a batch run / time-limit run: the repetitions or the time limit; while it runs,
  /// the current repetition ("3 / 10") or the time so far ("01:23 / 05:00").
  /// </summary>
  public override string Badge => Optimizer switch {
    BatchRun b when IsActive => $"{Math.Min(b.RepetitionsCounter + 1, b.Repetitions)} / {b.Repetitions}",
    BatchRun b => $"{b.Repetitions}×",
    TimeLimitRun t when IsActive => $"{Clock(t.ExecutionTime)} / {Clock(t.MaximumExecutionTime)}",
    TimeLimitRun t => ExperimentWorkspaceViewModel.FormatDuration(t.MaximumExecutionTime),
    _ => ""
  };

  public override bool HasBadge => Optimizer is BatchRun or TimeLimitRun;

  /// <summary>Running or paused: the badge counts.</summary>
  public bool IsActive => Optimizer.ExecutionState is ExecutionState.Started or ExecutionState.Paused;

  public override string BadgeTip => Optimizer switch {
    BatchRun b => IsActive ? $"Repetition {Math.Min(b.RepetitionsCounter + 1, b.Repetitions)} of {b.Repetitions}" : $"{b.Repetitions} repetitions",
    TimeLimitRun t => IsActive ? $"Running for {Clock(t.ExecutionTime)} of {Clock(t.MaximumExecutionTime)}" : "Time limit",
    _ => ""
  };

  private static string Clock(TimeSpan t) => t.TotalHours >= 1
    ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

  /// <summary>Counters while running (called by the workspace's live refresh).</summary>
  public void RefreshBadge() {
    OnPropertyChanged(nameof(Badge));
    OnPropertyChanged(nameof(BadgeTip));
  }

  public bool IsBatchRun => Optimizer is BatchRun;
  public bool IsTimeLimitRun => Optimizer is TimeLimitRun;

  public string RepetitionsText {
    get => (Optimizer as BatchRun)?.Repetitions.ToString(CultureInfo.InvariantCulture) ?? "";
    set {
      if (Optimizer is not BatchRun b) return;
      if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1) {
        b.Repetitions = n;
        MarkModified();
        RefreshBadge();
      } else Workspace.Status = "Repetitions must be a whole number of at least 1.";
      OnPropertyChanged();
    }
  }

  /// <summary>Time limit as "90", "90s", "5m", "1h" or "hh:mm:ss".</summary>
  public string TimeLimitText {
    get => Optimizer is TimeLimitRun t ? ExperimentWorkspaceViewModel.FormatDuration(t.MaximumExecutionTime) : "";
    set {
      if (Optimizer is not TimeLimitRun t) return;
      if (ExperimentWorkspaceViewModel.ParseDuration(value) is TimeSpan limit && limit > TimeSpan.Zero) {
        t.MaximumExecutionTime = limit;
        MarkModified();
        RefreshBadge();
      } else Workspace.Status = "Time limit: e.g. 90s, 5m, 1h or 00:10:00.";
      OnPropertyChanged();
    }
  }

  internal void ChildrenChanged() {
    OnPropertyChanged(nameof(CanAdd));
  }
}

/// <summary>A top-level experiment: can be selected for "Start selected experiments", saved and run.</summary>
public partial class ExperimentBlockViewModel(ExperimentWorkspaceViewModel workspace, Experiment experiment, string? filePath)
    : ContainerBlockViewModel(workspace, null, experiment) {
  public Experiment Experiment { get; } = experiment;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(IsSaved), nameof(StatePictogram), nameof(StateTip))]
  public partial string? FilePath { get; set; } = filePath;

  /// <summary>Changed since it was opened or saved (opened from a file: unchanged).</summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(IsSaved), nameof(StatePictogram), nameof(StateTip))]
  public partial bool IsModified { get; set; }

  public bool IsSaved => FilePath != null && !IsModified;
  public override string? StatePictogram => IsSaved ? "experiment_saved" : "experiment_unsaved";
  public override string StateTip => IsSaved ? $"Saved: {FilePath}" : FilePath == null ? "Not saved yet" : $"Changed since saved to {FilePath}";

  /// <summary>Checked experiments are started by "Start selected experiments".</summary>
  [ObservableProperty]
  public partial bool IsChecked { get; set; }

  partial void OnIsCheckedChanged(bool value) => Workspace.RefreshState();

  [ObservableProperty]
  public partial string RunStatus { get; set; } = "";

  /// <summary>Started with the start buttons and not done yet (its algorithms wait or work).</summary>
  [ObservableProperty]
  public partial bool IsRunning { get; set; }

  /// <summary>Why the experiment cannot start yet (empty when ready).</summary>
  [ObservableProperty]
  public partial string Missing { get; set; } = "";

  public bool IsReady => Missing.Length == 0;

  partial void OnMissingChanged(string value) => OnPropertyChanged(nameof(IsReady));

  [RelayCommand]
  private Task SaveAsync() => Workspace.SaveAsync(this);

  public override IReadOnlyList<MenuChoice> Menu =>
    [new("Save as...", SaveCommand), new("Show in Run tab", ShowInRunTabCommand),
     new("Rename", RenameCommand), new("Remove from workspace", DeleteCommand)];

  [RelayCommand]
  private void ShowInRunTab() => Workspace.ShowInRunTab(this);
}

/// <summary>An algorithm and, on the same row, its problem (or the "+ Add problem" button).</summary>
public partial class AlgorithmBlockViewModel : BlockViewModel {
  public AlgorithmBlockViewModel(ExperimentWorkspaceViewModel workspace, ContainerBlockViewModel parent, IAlgorithm algorithm)
      : base(workspace, parent) {
    Algorithm = algorithm;
    if (algorithm.Problem != null) Problem = new ProblemBlockViewModel(workspace, this, algorithm.Problem);
    Watch();
  }

  public IAlgorithm Algorithm { get; }
  public override INamedItem Item => Algorithm;
  public override string Pictogram => "Algorith_logo";

  // outcome of the last run, from the algorithm's events (raised on its thread)
  private volatile bool failed, stopRequested, aborted;
  private volatile string? failure;

  private void Watch() {
    Algorithm.Started += (_, _) => { failed = false; aborted = false; failure = null; StatusChanged(); };
    Algorithm.ExceptionOccurred += (_, e) => { failed = true; failure = e.Value.Message; StatusChanged(); };
    Algorithm.Stopped += (_, _) => { aborted = stopRequested; stopRequested = false; StatusChanged(); };
    Algorithm.Paused += (_, _) => StatusChanged();
    Algorithm.Prepared += (_, _) => StatusChanged();
  }

  private void StatusChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() => {
    RefreshStatus();
    MarkModified();  // results and runs are part of the saved file
  });

  /// <summary>The user stopped it: when it stops, it did not finish.</summary>
  public void StopRequested() {
    if (Algorithm.ExecutionState is ExecutionState.Started or ExecutionState.Paused) stopRequested = true;
  }

  /// <summary>
  /// Status pictogram: no problem (cannot run), never run or stopped before finishing, waiting
  /// (in a running experiment, or paused), working, finished, failed.
  /// </summary>
  public AlgorithmStatus Status {
    get {
      if (Algorithm.Problem == null) return AlgorithmStatus.ProblemMissing;
      return Algorithm.ExecutionState switch {
        ExecutionState.Started => AlgorithmStatus.Working,
        ExecutionState.Paused => AlgorithmStatus.Waiting,
        ExecutionState.Stopped when failed => AlgorithmStatus.Failed,
        ExecutionState.Stopped when aborted => AlgorithmStatus.NotRun,
        ExecutionState.Stopped => AlgorithmStatus.Finished,
        _ when failed => AlgorithmStatus.Failed,
        _ => Root is { IsRunning: true } ? AlgorithmStatus.Waiting : AlgorithmStatus.NotRun
      };
    }
  }

  public override bool FixedWidth => true;

  public override string? StatePictogram => Status switch {
    AlgorithmStatus.ProblemMissing => "Algorithm_status_Problem_occured",
    AlgorithmStatus.Waiting => "Algorithm_status_waiting_to_be_executed",
    AlgorithmStatus.Working => "Algorithm_status_working",
    AlgorithmStatus.Finished => "Algorithm_status_succesfull_finished",
    AlgorithmStatus.Failed => "Algorithm_status_failed",
    _ => "Algorithm_never_attempted_or_aborted"
  };

  public override string StateTip => Status switch {
    AlgorithmStatus.ProblemMissing => "Cannot run: no problem chosen",
    AlgorithmStatus.Waiting => Algorithm.ExecutionState == ExecutionState.Paused ? "Paused" : "Waiting to be executed",
    AlgorithmStatus.Working => "Running",
    AlgorithmStatus.Finished => "Finished successfully",
    AlgorithmStatus.Failed => $"Failed: {failure}",
    _ => aborted ? "Stopped before it finished" : "Not run yet"
  };

  public void RefreshStatus() {
    OnPropertyChanged(nameof(Status));
    OnPropertyChanged(nameof(StatePictogram));
    OnPropertyChanged(nameof(StateTip));
  }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(HasProblem), nameof(Menu))]
  public partial ProblemBlockViewModel? Problem { get; set; }

  public bool HasProblem => Problem != null;

  [RelayCommand]
  private Task ChooseProblemAsync() => Workspace.ChooseProblemAsync(this);

  private AlgorithmDetailViewModel? detail;

  /// <summary>The detail view (kept per block, so tabs and selections stay while switching blocks).</summary>
  public AlgorithmDetailViewModel Detail => detail ??= new AlgorithmDetailViewModel(this);

  public AlgorithmDetailViewModel ShowDetail(int tab) {
    Detail.SelectedTab = tab;
    return Detail;
  }

  /// <summary>Tree expansion as in HeuristicLab: problem, parameters, results and runs below the algorithm.</summary>
  [ObservableProperty]
  public partial bool IsExpanded { get; set; }

  [RelayCommand]
  private void ToggleExpanded() => IsExpanded = !IsExpanded;

  public string ParametersText => $"Parameters ({ItemInspector.Parameters(Algorithm).Count})";
  public string ResultsText => $"Results ({Algorithm.Results.Count})";
  public string RunsText => $"Runs ({Algorithm.Runs.Count})";

  partial void OnIsExpandedChanged(bool value) => RefreshCounts();

  public void RefreshCounts() {
    OnPropertyChanged(nameof(ParametersText));
    OnPropertyChanged(nameof(ResultsText));
    OnPropertyChanged(nameof(RunsText));
  }

  [RelayCommand]
  private void OpenTab(string tab) {
    Workspace.Selected = this;
    Detail.SelectedTab = int.Parse(tab, System.Globalization.CultureInfo.InvariantCulture);
  }

  [RelayCommand]
  private void ShowInRunTab() => Workspace.ShowInRunTab(this);

  public override IReadOnlyList<MenuChoice> Menu =>
    [new(HasProblem ? "Change problem..." : "Add problem...", ChooseProblemCommand), new("Show in Run tab", ShowInRunTabCommand),
     new("Rename", RenameCommand), new("Delete", DeleteCommand)];

  internal void SetProblem(IProblem? problem) {
    if (Problem?.IsSelected == true) Workspace.Selected = null;
    if (problem != null) {
      Algorithm.Problem = problem;
    } else {
      try {
        Algorithm.Problem = null;
      } catch (NullReferenceException) when (Algorithm.Problem == null) {
        // legacy algorithms assume a problem in OnProblemChanged; the problem is detached by then,
        // only re-preparing is skipped, and setting a new problem parameterizes everything again
      }
    }
    Problem = problem == null ? null : new ProblemBlockViewModel(Workspace, this, problem);
    detail?.RefreshProblem();
    MarkModified();
    RefreshStatus();
  }
}

/// <summary>The problem of one algorithm. Deleting it only clears that algorithm's problem.</summary>
public partial class ProblemBlockViewModel(ExperimentWorkspaceViewModel workspace, AlgorithmBlockViewModel owner, IProblem problem)
    : BlockViewModel(workspace, owner.Parent) {
  public AlgorithmBlockViewModel Owner { get; } = owner;
  public IProblem Problem { get; } = problem;
  public override INamedItem Item => Problem;
  public override string Pictogram => "Problem_logo";
  public override bool FixedWidth => true;

  /// <summary>"TSP: ch130": the abbreviation of the problem type (if it has one) and the instance name.</summary>
  public override string Label {
    get {
      if (Name == Kind) return Name;
      var abbreviation = System.Text.RegularExpressions.Regex.Match(Kind, @"\(([^)]+)\)\s*$");
      return $"{(abbreviation.Success ? abbreviation.Groups[1].Value : Kind)}: {Name}";
    }
  }

  [RelayCommand]
  private Task ChangeAsync() => Workspace.ChooseProblemAsync(Owner);

  public override IReadOnlyList<MenuChoice> Menu =>
    [new("Change problem...", ChangeCommand), new("Rename", RenameCommand), new("Remove problem", DeleteCommand)];
}
