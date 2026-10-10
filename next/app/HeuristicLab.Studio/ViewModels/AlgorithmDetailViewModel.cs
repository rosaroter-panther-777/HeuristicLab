using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeuristicLab.Core;
using HeuristicLab.Data;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Next.Runtime.Visuals;
using HeuristicLab.Optimization;
using HeuristicLab.Studio.Controls;

namespace HeuristicLab.Studio.ViewModels;

/// <summary>
/// Everything about one algorithm block once algorithm and problem are chosen: name, problem (source,
/// parameters, visualization), algorithm parameters, live results, runs, operator graph and engine,
/// plus start / pause / stop. Breakpoints on operators pause the algorithm; Start resumes it.
/// </summary>
public partial class AlgorithmDetailViewModel : ViewModelBase {
  public const int ProblemTab = 0, AlgorithmTab = 1, ResultsTab = 2, RunsTab = 3, GraphTab = 4, EngineTab = 5, AnalysisTab = 6;

  private readonly AlgorithmBlockViewModel block;
  private readonly EditContext context;
  private readonly DispatcherTimer timer;
  private Task? execution;
  private bool startedHere;

  public AlgorithmDetailViewModel(AlgorithmBlockViewModel block) {
    this.block = block;
    context = new EditContext(m => Message = m, () => CanEdit, block.MarkModified);
    Engines = ItemInspector.Engines();
    Results = new ResultsViewModel(() => Algorithm.Results, context);
    Runs = new RunsViewModel(Algorithm.Runs);
    Analysis = new AnalysisViewModel(() => Algorithm, running => block.Workspace.SetAlgorithmRunning(running));
    timer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => Tick());
    RefreshProblem();
  }

  public IAlgorithm Algorithm => block.Algorithm;

  /// <summary>Detailed analysis: a recorded run of a copy of the algorithm, to walk through step by step.</summary>
  public AnalysisViewModel Analysis { get; }
  public AlgorithmBlockViewModel Block => block;

  public string Name {
    get => block.Name;
    set { block.Name = value; OnPropertyChanged(); }
  }

  public string Kind => Algorithm.ItemName;

  [ObservableProperty]
  public partial int SelectedTab { get; set; } = AlgorithmTab;

  [ObservableProperty]
  public partial string? Message { get; set; }

  // ---- problem tab

  public bool HasProblem => Algorithm.Problem != null;

  [ObservableProperty]
  public partial ParameterListViewModel? ProblemParameters { get; set; }

  [ObservableProperty]
  public partial string ProblemSource { get; set; } = "";

  [ObservableProperty]
  public partial string ProblemDescription { get; set; } = "";

  /// <summary>Locations, best known solutions, landscapes or data of the problem instance.</summary>
  public VisualsViewModel ProblemVisuals { get; } = new();

  [RelayCommand]
  private Task ChooseProblemAsync() => block.Workspace.ChooseProblemAsync(block);

  /// <summary>
  /// Rebuilds what depends on the problem: the problem tab, and the algorithm's parameters and
  /// graph (an algorithm offers the operators its problem provides, e.g. move generators).
  /// </summary>
  public void RefreshProblem() {
    OnPropertyChanged(nameof(HasProblem));
    AlgorithmParameters = new ParameterListViewModel(Algorithm, context);
    Graph = ItemInspector.GraphOf(Algorithm) is OperatorGraph g ? new OperatorGraphViewModel(g, Algorithm.Name, context) : null;
    var problem = Algorithm.Problem;
    if (problem == null) {
      ProblemParameters = null;
      ProblemSource = ProblemDescription = "";
      ProblemVisuals.Show([]);
      return;
    }
    ProblemParameters = new ParameterListViewModel(problem, context);
    var origin = ProblemInstances.Origin(problem);
    ProblemSource = origin != null ? $"Library: {origin.Provider}    Instance: {origin.Name}" : "Not from an instance library";
    ProblemDescription = problem.Description;
    try {
      ProblemVisuals.Show(Visualizations.ForProblem(problem));
    } catch (Exception e) when (e is InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException) {
      ProblemVisuals.Show([new TextVisual("Visualization", "This problem could not be drawn: " + e.Message)]);
    }
  }

  // ---- algorithm, results, runs, graph

  [ObservableProperty]
  public partial ParameterListViewModel AlgorithmParameters { get; set; } = null!;

  public ResultsViewModel Results { get; }
  public RunsViewModel Runs { get; }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(HasGraph))]
  public partial OperatorGraphViewModel? Graph { get; set; }

  public bool HasGraph => Graph != null;

  // ---- engine

  public IReadOnlyList<CatalogEntry> Engines { get; }
  public bool HasEngine => Algorithm is EngineAlgorithm;

  public CatalogEntry? SelectedEngine {
    get => Algorithm is EngineAlgorithm { Engine: IEngine e } ? Engines.FirstOrDefault(x => x.Type == e.GetType()) : null;
    set {
      if (value != null && Algorithm is EngineAlgorithm a && a.Engine?.GetType() != value.Type) {
        if (CanEdit) { a.Engine = (IEngine)value.CreateInstance(); block.MarkModified(); RefreshLog(); }
        else Message = "The engine can be changed when the algorithm is not running.";
      }
      OnPropertyChanged();
    }
  }

  public ObservableCollection<string> Log { get; } = [];

  [RelayCommand]
  private void ClearLog() {
    (Algorithm as EngineAlgorithm)?.Engine?.Log.Clear();
    RefreshLog();
  }

  private void RefreshLog() {
    var messages = (Algorithm as EngineAlgorithm)?.Engine?.Log.Messages.ToList() ?? [];
    if (messages.Count == Log.Count) return;
    Log.Clear();
    foreach (var m in messages) Log.Add(m);
  }

  // ---- execution

  public string State => Algorithm.ExecutionState.ToString();
  public string ExecutionTime => Algorithm.ExecutionTime.ToString(@"hh\:mm\:ss\.f", CultureInfo.InvariantCulture);

  /// <summary>Parameters can change only while the algorithm is prepared or stopped and no experiment runs.</summary>
  public bool CanEdit => Algorithm.ExecutionState is ExecutionState.Prepared or ExecutionState.Stopped && !block.Workspace.IsRunningExperiments;

  [RelayCommand(CanExecute = nameof(CanStart))]
  private void Start() {
    if (Algorithm.Problem == null) { Message = "Add a problem first."; return; }
    Message = HasBreakpoints() && Algorithm is EngineAlgorithm { Engine: not null } a && a.Engine.GetType().Name != "DebugEngine"
      ? "Breakpoints only pause the Debug Engine (Engine tab)." : null;
    if (Algorithm.ExecutionState == ExecutionState.Stopped) Algorithm.Prepare(clearRuns: false);
    HeuristicLab.Next.Runtime.Resources.Configure(Algorithm);
    block.Workspace.SetAlgorithmRunning(true);
    startedHere = true;
    execution = Task.Run(() => Algorithm.Start(CancellationToken.None));
    timer.Start();
    Tick();
  }
  private bool CanStart() => Algorithm.ExecutionState is ExecutionState.Prepared or ExecutionState.Paused or ExecutionState.Stopped
                             && Algorithm.Problem != null && !block.Workspace.IsRunningExperiments
                             && (execution == null || execution.IsCompleted || Algorithm.ExecutionState == ExecutionState.Paused);

  [RelayCommand(CanExecute = nameof(IsStarted))]
  private void Pause() => Algorithm.Pause();
  // while an experiment runs the algorithm, the experiment's Stop controls it
  private bool IsStarted() => Algorithm.ExecutionState == ExecutionState.Started && !block.Workspace.IsRunningExperiments;

  [RelayCommand(CanExecute = nameof(CanStop))]
  private void Stop() {
    block.StopRequested();
    Algorithm.Stop();
  }
  private bool CanStop() => Algorithm.ExecutionState is ExecutionState.Started or ExecutionState.Paused && !block.Workspace.IsRunningExperiments;

  [RelayCommand(CanExecute = nameof(CanPrepare))]
  private void Prepare() {
    Algorithm.Prepare(clearRuns: false);
    Tick();
  }
  private bool CanPrepare() => Algorithm.ExecutionState is ExecutionState.Stopped or ExecutionState.Paused && !block.Workspace.IsRunningExperiments;

  private bool HasBreakpoints() =>
    ItemInspector.GraphOf(Algorithm) is OperatorGraph g && Operators(g, []).Any(o => o.Breakpoint);

  private static IEnumerable<IOperator> Operators(OperatorGraph graph, HashSet<OperatorGraph> seen) =>
    !seen.Add(graph) ? [] : graph.Operators.SelectMany(o => ItemInspector.GraphOf(o) is OperatorGraph inner ? Operators(inner, seen).Prepend(o) : [o]);

  /// <summary>Waits until the algorithm stops or pauses (for tests and callers that need the result).</summary>
  public async Task WaitAsync() {
    if (execution != null) await execution;
    Tick();
  }

  private void Tick() {
    OnPropertyChanged(nameof(State));
    OnPropertyChanged(nameof(ExecutionTime));
    OnPropertyChanged(nameof(CanEdit));
    StartCommand.NotifyCanExecuteChanged();
    PauseCommand.NotifyCanExecuteChanged();
    StopCommand.NotifyCanExecuteChanged();
    PrepareCommand.NotifyCanExecuteChanged();
    Results.Refresh();
    Runs.Refresh();
    if (Analysis.IsRecording) Analysis.Refresh();
    RefreshLog();
    block.RefreshCounts();
    bool active = Algorithm.ExecutionState is ExecutionState.Started || execution is { IsCompleted: false };
    if (!active) {
      timer.Stop();
      if (startedHere) {
        HeuristicLab.Next.Runtime.Resources.Tag(Algorithm.Runs);
        // paused (e.g. at a breakpoint) still counts as running for the workspace: the tree stays locked
        bool paused = Algorithm.ExecutionState == ExecutionState.Paused;
        startedHere = paused;
        block.Workspace.SetAlgorithmRunning(paused);
        if (paused) Message = "Paused (breakpoint or Pause). Start resumes.";
      }
    }
  }

  /// <summary>Called when the workspace's run state changes.</summary>
  public void Refresh() => Tick();
}

/// <summary>One named result; its value text changes in place while the algorithm runs.</summary>
public partial class ResultEntryViewModel(string name, IItem? value, string summary) : ViewModelBase {
  public string Name { get; } = name;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(KindPictogram), nameof(KindTip))]
  public partial IItem? Value { get; set; } = value;

  private Type? kindOf;
  private string kind = "optional_value_parameter";

  /// <summary>
  /// Pictogram of what the result is: a chart (tables, histograms, scatter plots: result_diagram_logo),
  /// a picture (tours, packings, trees, ...: result_visualisation_logo) or a value (the optional value
  /// parameter's pictogram). Decided once per value type, since live results are replaced by new
  /// values of the same type.
  /// </summary>
  public string KindPictogram {
    get {
      if (Value?.GetType() != kindOf) {
        kindOf = Value?.GetType();
        IReadOnlyList<HeuristicLab.Next.Runtime.Visuals.Visual> visuals;
        try {
          visuals = HeuristicLab.Next.Runtime.Visuals.Visualizations.For(Value, Name);
        } catch (Exception e) when (e is InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException) {
          visuals = [];  // changed while the algorithm runs
          kindOf = null;
        }
        kind = visuals.Count == 0 ? "optional_value_parameter"
          : visuals[0] is HeuristicLab.Next.Runtime.Visuals.ChartVisual ? "result_diagram_logo" : "result_visualisation_logo";
      }
      return kind;
    }
  }

  public string KindTip => KindPictogram switch {
    "result_diagram_logo" => "Chart",
    "result_visualisation_logo" => "Visualization",
    _ => "Value"
  };

  [ObservableProperty]
  public partial string Summary { get; set; } = summary;
}

/// <summary>
/// Results of an algorithm as a live monitor: entries are updated in place (selection and
/// scrolling stay), and the selected result's detail (quality, visualization, value) follows its
/// data while the algorithm runs. Read through a function: preparing an algorithm gives it a new
/// result collection.
/// </summary>
public partial class ResultsViewModel(Func<ResultCollection> results, EditContext context) : ViewModelBase {
  public ObservableCollection<ResultEntryViewModel> Entries { get; } = [];

  [ObservableProperty]
  public partial ResultEntryViewModel? Selected { get; set; }

  [ObservableProperty]
  public partial ResultDetailViewModel? Detail { get; set; }

  public bool IsEmpty => Entries.Count == 0;

  partial void OnSelectedChanged(ResultEntryViewModel? value) =>
    Detail = value == null ? null : new ResultDetailViewModel(value.Name, value.Value, context);

  /// <summary>Syncs the entries with the algorithm's results and refreshes the selected detail.</summary>
  public void Refresh() {
    List<(string Name, IItem? Value, string Summary)> current;
    try {
      current = results().ToArray().Select(r => (r.Name, (IItem?)r.Value, ItemInspector.Summary(r.Value))).ToList();
    } catch (InvalidOperationException) {
      return;  // changed while the algorithm runs; next tick
    }
    // results only ever get added during a run, and replaced wholesale by Prepare
    if (!current.Select(c => c.Name).Take(Entries.Count).SequenceEqual(Entries.Select(e => e.Name))) {
      Entries.Clear();
      Selected = null;
    }
    for (int i = 0; i < current.Count; i++) {
      var (name, value, summary) = current[i];
      if (i < Entries.Count) {
        Entries[i].Value = value;
        Entries[i].Summary = summary;
      } else Entries.Add(new ResultEntryViewModel(name, value, summary));
    }
    OnPropertyChanged(nameof(IsEmpty));
    if (Selected != null) {
      if (Detail == null || !ReferenceEquals(Detail.Item, Selected.Value)) Detail = new ResultDetailViewModel(Selected.Name, Selected.Value, context);
      else Detail.Refresh();
    }
  }
}

/// <summary>
/// The selected result by data type: a quality and type-specific pictures (see the runtime's
/// Visualizations: tours, routes, packings in 2D and 3D, expression trees, ant trails, schedules,
/// landscapes, tables with their chart types, data analysis plots), and the underlying value (its
/// members for structured results, a table or text otherwise). Rebuilt from the live item on every refresh,
/// so visualization and quality always belong to the same solution.
/// </summary>
public partial class ResultDetailViewModel : ViewModelBase {
  private readonly EditContext context;

  public ResultDetailViewModel(string name, IItem? item, EditContext context) {
    Name = name;
    Item = item;
    this.context = context;
    Refresh();
  }

  public string Name { get; }
  public IItem? Item { get; }
  public string TypeName => Item?.ItemName ?? "";

  [ObservableProperty]
  public partial string? Quality { get; set; }

  public bool HasQuality => Quality != null;

  /// <summary>Type-specific pictures: tours, routes, packings, trees, trails, charts, ...</summary>
  public VisualsViewModel Visuals { get; } = new();

  public bool HasVisualization => Visuals.HasAny;

  /// <summary>Visualization or Value, kept across refreshes.</summary>
  [ObservableProperty]
  public partial int SelectedTab { get; set; }

  /// <summary>Underlying value: the members of a structured result, or the value itself.</summary>
  [ObservableProperty]
  public partial IReadOnlyList<MemberViewModel> Members { get; set; } = [];

  partial void OnQualityChanged(string? value) => OnPropertyChanged(nameof(HasQuality));

  public void Refresh() {
    if (Item == null) { Members = [new MemberViewModel("Value", new ValueEditorViewModel(null, context, readOnly: true))]; return; }
    try {
      Quality = ItemInspector.QualityOf(Item)?.ToString("G10", CultureInfo.InvariantCulture);
      bool had = HasVisualization;
      Visuals.Show(Visualizations.For(Item, Name));
      if (had != HasVisualization) OnPropertyChanged(nameof(HasVisualization));
      var members = ItemInspector.KindOf(Item) is ValueKind.Other or ValueKind.Parameterized ? ItemInspector.Members(Item) : [];
      Members = members.Count > 0
        ? members.Select(m => new MemberViewModel(m.Name, new ValueEditorViewModel(m.Value, context, readOnly: true))).ToList()
        : [new MemberViewModel("Value", new ValueEditorViewModel(Item, context, readOnly: true))];
      if (!HasVisualization) SelectedTab = 1;
    } catch (Exception e) when (e is InvalidOperationException or ArgumentException or IndexOutOfRangeException
                                       or NullReferenceException or System.Collections.Generic.KeyNotFoundException) {
      // the running algorithm changed the value while it was read; the next refresh shows it
    }
  }
}

public sealed record MemberViewModel(string Name, ValueEditorViewModel Value);

public sealed record RunTableRow(string Name, IReadOnlyList<string> Cells, IRun Run);

/// <summary>Runs as a table: one row per run, one column per scalar result.</summary>
public partial class RunsViewModel(RunCollection runs) : ViewModelBase {
  private int shown = -1;

  [ObservableProperty]
  public partial IReadOnlyList<string> Columns { get; set; } = [];

  [ObservableProperty]
  public partial IReadOnlyList<RunTableRow> Rows { get; set; } = [];

  [ObservableProperty]
  public partial RunTableRow? Selected { get; set; }

  [ObservableProperty]
  public partial IReadOnlyList<NameValue> SelectedDetails { get; set; } = [];

  public bool IsEmpty => Rows.Count == 0;

  partial void OnRowsChanged(IReadOnlyList<RunTableRow> value) => OnPropertyChanged(nameof(IsEmpty));

  partial void OnSelectedChanged(RunTableRow? value) {
    if (value == null) { SelectedDetails = []; return; }
    var record = OptimizerRunner.ToRecord(value.Run);
    SelectedDetails = record.Parameters.OrderBy(p => p.Key).Select(p => new NameValue("Parameter: " + p.Key, Text(p.Value)))
      .Concat(record.Results.OrderBy(r => r.Key).Select(r => new NameValue("Result: " + r.Key, Text(r.Value)))).ToList();
  }

  private static string Text(ItemValue v) => v.Value switch {
    double d => d.ToString("G10", CultureInfo.InvariantCulture),
    null => v.Text ?? "",
    Array a => $"{v.Type} [{a.Length}]",
    TableValue t => $"{t.Name} ({t.Rows.Count} rows)",
    var o => Convert.ToString(o, CultureInfo.InvariantCulture) ?? ""
  };

  public void Refresh() {
    IRun[] all;
    try { all = runs.ToArray(); } catch (InvalidOperationException) { return; }
    if (all.Length == shown) return;
    shown = all.Length;
    var records = all.Select(r => (Run: r, Record: OptimizerRunner.ToRecord(r))).ToList();
    var columns = records.SelectMany(r => r.Record.Results.Where(x => x.Value.Value is double or int or long or bool).Select(x => x.Key))
      .Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
    var selected = Selected?.Run;
    Columns = columns;
    Rows = records.Select(r => new RunTableRow(r.Run.Name,
      columns.Select(c => r.Record.Results.TryGetValue(c, out var v) ? Text(v) : "").ToList(), r.Run)).ToList();
    Selected = Rows.FirstOrDefault(r => ReferenceEquals(r.Run, selected));
  }
}
