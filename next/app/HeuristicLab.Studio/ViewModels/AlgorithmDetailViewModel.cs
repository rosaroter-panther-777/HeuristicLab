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
using HeuristicLab.Optimization;
using HeuristicLab.Studio.Controls;

namespace HeuristicLab.Studio.ViewModels;

/// <summary>
/// Everything about one algorithm block once algorithm and problem are chosen: name, problem (source,
/// parameters, visualization), algorithm parameters, live results, runs, operator graph and engine,
/// plus start / pause / stop. Breakpoints on operators pause the algorithm; Start resumes it.
/// </summary>
public partial class AlgorithmDetailViewModel : ViewModelBase {
  public const int ProblemTab = 0, AlgorithmTab = 1, ResultsTab = 2, RunsTab = 3, GraphTab = 4, EngineTab = 5;

  private readonly AlgorithmBlockViewModel block;
  private readonly EditContext context;
  private readonly DispatcherTimer timer;
  private Task? execution;

  public AlgorithmDetailViewModel(AlgorithmBlockViewModel block) {
    this.block = block;
    context = new EditContext(m => Message = m, () => CanEdit);
    Engines = ItemInspector.Engines();
    Results = new ResultsViewModel(() => Algorithm.Results, context);
    Runs = new RunsViewModel(Algorithm.Runs);
    timer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => Tick());
    RefreshProblem();
  }

  public IAlgorithm Algorithm => block.Algorithm;
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

  [ObservableProperty]
  public partial IReadOnlyList<ChartSeries> ProblemChart { get; set; } = [];

  [ObservableProperty]
  public partial string ProblemChartTitle { get; set; } = "";

  public bool HasProblemChart => ProblemChart.Count > 0;

  partial void OnProblemChartChanged(IReadOnlyList<ChartSeries> value) => OnPropertyChanged(nameof(HasProblemChart));

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
      ProblemSource = ProblemDescription = ProblemChartTitle = "";
      ProblemChart = [];
      return;
    }
    ProblemParameters = new ParameterListViewModel(problem, context);
    var origin = ProblemInstances.Origin(problem);
    ProblemSource = origin != null ? $"Library: {origin.Provider}    Instance: {origin.Name}" : "Not from an instance library";
    ProblemDescription = problem.Description;
    BuildProblemChart(problem);
  }

  /// <summary>Problems with coordinates (TSP, VRP, ...): the locations and, if known, the best known tour.</summary>
  private void BuildProblemChart(IProblem problem) {
    ProblemChart = [];
    ProblemChartTitle = "";
    if (!problem.Parameters.TryGetValue("Coordinates", out var cp) || ItemInspector.Value(cp) is not DoubleMatrix { Columns: >= 2 } xy) return;
    var points = Enumerable.Range(0, xy.Rows).Select(r => new ChartPoint(xy[r, 0], xy[r, 1])).ToList();
    var series = new List<ChartSeries>();
    if (problem.Parameters.TryGetValue("BestKnownSolution", out var sp) && ItemInspector.Value(sp) is IntArray tour && tour.Length == points.Count) {
      var route = tour.Select(i => points[i]).Append(points[tour[0]]).ToList();
      series.Add(new ChartSeries("Best known solution", ChartSeries.PaletteColor(0), route));
    }
    series.Add(new ChartSeries("Locations", ChartSeries.PaletteColor(1), points, PointsOnly: true));
    ProblemChart = series;
    ProblemChartTitle = problem.Parameters.TryGetValue("BestKnownQuality", out var qp) && ItemInspector.Value(qp) is DoubleValue q
      ? $"Best known quality: {q.Value.ToString("G10", CultureInfo.InvariantCulture)}" : "";
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
        if (CanEdit) { a.Engine = (IEngine)value.CreateInstance(); RefreshLog(); }
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
    block.Workspace.SetAlgorithmRunning(true);
    execution = Task.Run(() => Algorithm.Start(CancellationToken.None));
    timer.Start();
    Tick();
  }
  private bool CanStart() => Algorithm.ExecutionState is ExecutionState.Prepared or ExecutionState.Paused or ExecutionState.Stopped
                             && Algorithm.Problem != null && !block.Workspace.IsRunningExperiments
                             && (execution == null || execution.IsCompleted || Algorithm.ExecutionState == ExecutionState.Paused);

  [RelayCommand(CanExecute = nameof(IsStarted))]
  private void Pause() => Algorithm.Pause();
  private bool IsStarted() => Algorithm.ExecutionState == ExecutionState.Started;

  [RelayCommand(CanExecute = nameof(CanStop))]
  private void Stop() => Algorithm.Stop();
  private bool CanStop() => Algorithm.ExecutionState is ExecutionState.Started or ExecutionState.Paused;

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
    RefreshLog();
    block.RefreshCounts();
    bool active = Algorithm.ExecutionState is ExecutionState.Started || execution is { IsCompleted: false };
    if (!active) {
      timer.Stop();
      // paused (e.g. at a breakpoint) still counts as running for the workspace: the tree stays locked
      block.Workspace.SetAlgorithmRunning(Algorithm.ExecutionState == ExecutionState.Paused);
      if (Algorithm.ExecutionState == ExecutionState.Paused) Message = "Paused (breakpoint or Pause). Start resumes.";
    }
  }

  /// <summary>Called when the workspace's run state changes.</summary>
  public void Refresh() => Tick();
}

public sealed record ResultEntry(string Name, string Summary, IItem? Value);

/// <summary>
/// Results of an algorithm, refreshed while it runs; the selected one is shown in detail. Read
/// through a function: preparing an algorithm gives it a new result collection.
/// </summary>
public partial class ResultsViewModel(Func<ResultCollection> results, EditContext context) : ViewModelBase {
  public ObservableCollection<ResultEntry> Entries { get; } = [];

  [ObservableProperty]
  public partial ResultEntry? Selected { get; set; }

  [ObservableProperty]
  public partial ValueEditorViewModel? SelectedValue { get; set; }

  [ObservableProperty]
  public partial IReadOnlyList<ChartSeries> Chart { get; set; } = [];

  public bool HasChart => Chart.Count > 0;
  public bool IsEmpty => Entries.Count == 0;

  [ObservableProperty]
  public partial string ChartXAxisTitle { get; set; } = "";

  [ObservableProperty]
  public partial string ChartYAxisTitle { get; set; } = "";

  partial void OnChartChanged(IReadOnlyList<ChartSeries> value) => OnPropertyChanged(nameof(HasChart));

  partial void OnSelectedChanged(ResultEntry? value) {
    SelectedValue = value == null ? null : new ValueEditorViewModel(value.Value, context, readOnly: true);
    var table = value?.Value is HeuristicLab.Analysis.DataTable ? ItemValues.From(value.Value).Value as TableValue : null;
    // tables without an axis title (e.g. Qualities) are indexed by row position
    ChartXAxisTitle = string.IsNullOrEmpty(table?.XAxisTitle) ? "Index" : table.XAxisTitle;
    ChartYAxisTitle = table?.YAxisTitle ?? "";
    Chart = table == null ? []
      : table.Rows.Select((r, i) => new ChartSeries(r.Key, ChartSeries.PaletteColor(i), r.Value.Select((y, x) => new ChartPoint(x, y)).ToList())).ToList();
  }

  public void Refresh() {
    List<ResultEntry> current;
    try {
      current = results().ToArray().Select(r => new ResultEntry(r.Name, ItemInspector.Summary(r.Value), r.Value)).ToList();
    } catch (InvalidOperationException) {
      return;  // changed while the algorithm runs; next tick
    }
    var selectedName = Selected?.Name;
    if (current.Select(c => (c.Name, c.Summary)).SequenceEqual(Entries.Select(e => (e.Name, e.Summary)))) return;
    Entries.Clear();
    foreach (var c in current) Entries.Add(c);
    OnPropertyChanged(nameof(IsEmpty));
    var again = Entries.FirstOrDefault(e => e.Name == selectedName);
    if (again != null && !ReferenceEquals(again.Value, Selected?.Value)) Selected = again;
    else if (again != null) { Selected = again; }
  }
}

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
    Columns = columns;
    Rows = records.Select(r => new RunTableRow(r.Run.Name,
      columns.Select(c => r.Record.Results.TryGetValue(c, out var v) ? Text(v) : "").ToList(), r.Run)).ToList();
  }
}
