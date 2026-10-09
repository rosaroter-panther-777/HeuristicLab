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
      OnPropertyChanged();
      OnPropertyChanged(nameof(Label));
    }
  }

  /// <summary>Text on the block.</summary>
  public virtual string Label => Name;

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
  public ObservableCollection<BlockViewModel> Children { get; } = [];
  public IReadOnlyList<MenuChoice> AddChoices { get; }

  public override IReadOnlyList<MenuChoice> Menu =>
    [new("Show in Run tab", new RelayCommand(() => Workspace.ShowInRunTab(this))), .. base.Menu];

  /// <summary>A time-limit run holds one algorithm, so its add button goes away once it has one.</summary>
  public bool CanAdd => Optimizer is not TimeLimitRun || Children.Count == 0;

  /// <summary>Only algorithms go into a time-limit run.</summary>
  public bool AcceptsContainers => Optimizer is not TimeLimitRun;

  public string AddText => Optimizer is TimeLimitRun ? "+ Add algorithm" : "+ Add batch run / time-limit run / algorithm";

  public override string Label => Optimizer switch {
    BatchRun b => $"{Name} ({b.Repetitions}×)",
    TimeLimitRun t => $"{Name} ({ExperimentWorkspaceViewModel.FormatDuration(t.MaximumExecutionTime)})",
    _ => Name
  };

  public bool IsBatchRun => Optimizer is BatchRun;
  public bool IsTimeLimitRun => Optimizer is TimeLimitRun;

  public string RepetitionsText {
    get => (Optimizer as BatchRun)?.Repetitions.ToString(CultureInfo.InvariantCulture) ?? "";
    set {
      if (Optimizer is not BatchRun b) return;
      if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1) {
        b.Repetitions = n;
        RefreshLabel();
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
        RefreshLabel();
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
  public partial string? FilePath { get; set; } = filePath;

  /// <summary>Checked experiments are started by "Start selected experiments".</summary>
  [ObservableProperty]
  public partial bool IsChecked { get; set; }

  partial void OnIsCheckedChanged(bool value) => Workspace.RefreshState();

  [ObservableProperty]
  public partial string RunStatus { get; set; } = "";

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
  }

  public IAlgorithm Algorithm { get; }
  public override INamedItem Item => Algorithm;

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
  }
}

/// <summary>The problem of one algorithm. Deleting it only clears that algorithm's problem.</summary>
public partial class ProblemBlockViewModel(ExperimentWorkspaceViewModel workspace, AlgorithmBlockViewModel owner, IProblem problem)
    : BlockViewModel(workspace, owner.Parent) {
  public AlgorithmBlockViewModel Owner { get; } = owner;
  public IProblem Problem { get; } = problem;
  public override INamedItem Item => Problem;

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
