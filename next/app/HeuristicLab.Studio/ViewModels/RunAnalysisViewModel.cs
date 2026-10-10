using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeuristicLab.Core;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Next.Runtime.Runs;
using HeuristicLab.Optimization;
using HeuristicLab.Studio.Services;

namespace HeuristicLab.Studio.ViewModels;

/// <summary>Where runs come from: an experiment, batch run, time-limit run or algorithm of the workspace, or a results folder.</summary>
public partial class RunSourceViewModel(RunAnalysisViewModel owner, string name, string kind, IOptimizer? optimizer, string? folder) : ViewModelBase {
  public string Name { get; } = name;
  public string Kind { get; } = kind;
  public IOptimizer? Optimizer { get; } = optimizer;
  public string? Folder { get; } = folder;
  public ObservableCollection<RunSourceViewModel> Children { get; } = [];
  public RunSourceViewModel? Parent { get; init; }

  public int RunCount => Optimizer?.Runs.Count ?? owner.FolderRunCount(Folder);
  public string Label => $"{Name}  ({RunCount} run{(RunCount == 1 ? "" : "s")})";

  [ObservableProperty]
  public partial bool IsChecked { get; set; }

  partial void OnIsCheckedChanged(bool value) => owner.Rebuild();

  public string Path => Parent == null ? Name : Parent.Path + " / " + Name;
}

public sealed record ColumnChoice(string Key, string Display) {
  public override string ToString() => Display;
}

public partial class FilterViewModel(RunAnalysisViewModel owner) : ViewModelBase {
  [ObservableProperty]
  public partial ColumnChoice? Column { get; set; }

  [ObservableProperty]
  public partial string Op { get; set; } = "=";

  [ObservableProperty]
  public partial string Value { get; set; } = "";

  public IReadOnlyList<string> Operators => RunFilter.Operators;
  public RunAnalysisViewModel Owner => owner;

  partial void OnColumnChanged(ColumnChoice? value) => owner.Rebuild();
  partial void OnOpChanged(string value) => owner.Rebuild();
  partial void OnValueChanged(string value) => owner.Rebuild();

  public RunFilter? Filter => Column == null || Value.Length == 0 ? null : new RunFilter(Column.Key, Op, Value);
}

public sealed record AnalysisRow(RunRow Row, IReadOnlyList<string> Cells);

public sealed record SummaryLine(string Group, string Count, string Mean, string StdDev, string Min, string Q1, string Median, string Q3, string Max);

public sealed record PairLine(string Pair, string MannWhitney, string Adjusted, string TTest, string CohensD, string HedgesG, bool Significant);

/// <summary>
/// "Results" tab: the runs of any experiments, batch runs, time-limit runs and algorithms of the
/// workspace (and of results folders) as one table, filtered and grouped by any combination of
/// columns, and compared in charts (box plots, scatter, histograms, cumulative distributions,
/// curves of every run) and with statistical tests - HeuristicLab's run collection views, with
/// grouping as a first-class choice instead of a color or modifier.
/// </summary>
public partial class RunAnalysisViewModel : ViewModelBase {
  private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
  private readonly ExperimentWorkspaceViewModel workspace;
  private readonly IFileDialogService? fileDialogs;
  private readonly List<string> folders = [];
  private readonly Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>> folderRows = [];
  private bool rebuilding;

  public RunAnalysisViewModel(ExperimentWorkspaceViewModel workspace, IFileDialogService? fileDialogs = null) {
    this.workspace = workspace;
    this.fileDialogs = fileDialogs;
  }

  public ObservableCollection<RunSourceViewModel> Sources { get; } = [];

  public const int TableView = 0, ChartsView = 1, StatisticsView = 2, RunView = 3;

  /// <summary>Table, Charts, Statistics or Run.</summary>
  [ObservableProperty]
  public partial int SelectedView { get; set; }

  [ObservableProperty]
  public partial RunTable Table { get; set; } = new([]);

  [ObservableProperty]
  public partial string Status { get; set; } = "Tick the experiments, batch runs, time-limit runs or algorithms whose runs you want to analyse.";

  public ObservableCollection<FilterViewModel> Filters { get; } = [];

  /// <summary>Columns grouped by, in order (several: every combination is a group).</summary>
  public ObservableCollection<ColumnChoice> GroupBy { get; } = [];

  [ObservableProperty]
  public partial decimal Bins { get; set; } = 5;

  [ObservableProperty]
  public partial IReadOnlyList<ColumnChoice> AllColumns { get; set; } = [];

  [ObservableProperty]
  public partial IReadOnlyList<ColumnChoice> NumericColumns { get; set; } = [];

  [ObservableProperty]
  public partial ColumnChoice? GroupToAdd { get; set; }

  public IReadOnlyList<RunGroup> Groups { get; private set; } = [];

  public int FolderRunCount(string? folder) => folder != null && folderRows.TryGetValue(folder, out var rows) ? rows.Count : 0;

  // ---- sources

  /// <summary>Re-reads the workspace (new experiments, finished runs); ticked sources stay ticked.</summary>
  [RelayCommand]
  public void RefreshSources() {
    var ticked = AllSources(Sources).Where(s => s.IsChecked).Select(s => (object?)s.Optimizer ?? s.Folder).ToHashSet();
    rebuilding = true;
    Sources.Clear();
    foreach (var experiment in workspace.Experiments) Sources.Add(Node(experiment, null, ticked));
    foreach (var folder in folders) {
      var node = new RunSourceViewModel(this, Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)), "Results folder", null, folder) { IsChecked = ticked.Contains(folder) };
      Sources.Add(node);
    }
    rebuilding = false;
    Rebuild();
  }

  private RunSourceViewModel Node(BlockViewModel block, RunSourceViewModel? parent, HashSet<object> ticked) {
    var optimizer = block switch { ContainerBlockViewModel c => c.Optimizer, AlgorithmBlockViewModel a => (IOptimizer)a.Algorithm, _ => null };
    string kind = optimizer switch { Experiment => "Experiment", BatchRun => "Batch run", TimeLimitRun => "Time-limit run", _ => "Algorithm" };
    var node = new RunSourceViewModel(this, block.Name, kind, optimizer, null) { Parent = parent, IsChecked = optimizer != null && ticked.Contains(optimizer) };
    if (block is ContainerBlockViewModel container)
      foreach (var child in container.Children) node.Children.Add(Node(child, node, ticked));
    return node;
  }

  private static IEnumerable<RunSourceViewModel> AllSources(IEnumerable<RunSourceViewModel> nodes) =>
    nodes.SelectMany(n => AllSources(n.Children).Prepend(n));

  [RelayCommand]
  private async Task AddFolderAsync() {
    if (fileDialogs != null && await fileDialogs.PickFolderAsync("Add results folder") is string folder) AddFolder(folder);
  }

  public void AddFolder(string folder) {
    try {
      folderRows[folder] = new ResultStore(folder).Rows();
    } catch (Exception e) {
      Status = $"Cannot read {folder}: {e.Message}";
      return;
    }
    if (!folders.Contains(folder)) folders.Add(folder);
    RefreshSources();
    AllSources(Sources).First(s => s.Folder == folder).IsChecked = true;
  }

  // ---- filters and grouping

  [RelayCommand]
  private void AddFilter() => Filters.Add(new FilterViewModel(this) { Column = AllColumns.FirstOrDefault() });

  [RelayCommand]
  private void RemoveFilter(FilterViewModel filter) {
    Filters.Remove(filter);
    Rebuild();
  }

  partial void OnGroupToAddChanged(ColumnChoice? value) {
    if (value == null) return;
    if (!GroupBy.Any(g => g.Key == value.Key)) GroupBy.Add(value);
    GroupToAdd = null;
    Rebuild();
  }

  [RelayCommand]
  private void RemoveGroup(ColumnChoice group) {
    GroupBy.Remove(group);
    Rebuild();
  }

  partial void OnBinsChanged(decimal value) => Rebuild();

  /// <summary>Builds table, groups and every view from the ticked sources, filters and grouping.</summary>
  public void Rebuild() {
    if (rebuilding) return;
    var ticked = AllSources(Sources).Where(s => s.IsChecked).ToList();
    // where each run was made: the deepest optimizer of the tree that holds it
    var origin = new Dictionary<IRun, string>(ReferenceEqualityComparer.Instance);
    foreach (var node in AllSources(Sources).Where(s => s.Optimizer != null))
      foreach (var run in node.Optimizer!.Runs) origin[run] = node.Path;
    var tables = new List<RunTable> {
      RunTable.FromRuns(ticked.Where(s => s.Optimizer != null)
        .SelectMany(s => s.Optimizer!.Runs.ToArray().Select(r => new SourcedRun(s.Name, Relative(origin.GetValueOrDefault(r, s.Path), s), r))))
    };
    tables.AddRange(ticked.Where(s => s.Folder != null && folderRows.ContainsKey(s.Folder!)).Select(s => RunTable.FromStore(s.Name, folderRows[s.Folder!])));
    var all = RunTable.Combine(tables);
    AllColumns = all.Columns.Select(c => new ColumnChoice(c.Key, c.ToString())).ToList();
    NumericColumns = all.Columns.Where(c => c.IsNumeric).Select(c => new ColumnChoice(c.Key, c.ToString())).ToList();
    Table = all.Where(Filters.Select(f => f.Filter).OfType<RunFilter>());
    Groups = RunGrouping.Group(Table, GroupBy.Select(g => g.Key).ToList(), (int)Bins);
    Status = ticked.Count == 0 ? "Tick the experiments, batch runs, time-limit runs or algorithms whose runs you want to analyse."
      : $"{Table.Rows.Count} of {all.Rows.Count} runs, {Groups.Count} group{(Groups.Count == 1 ? "" : "s")}";
    ChooseDefaults();
    RebuildRows();
    RebuildChart();
    RebuildStatistics();
  }

  /// <summary>Origin below the ticked source ("Batch run 1 / GA" under "Experiment 1"); the source itself if the run is its own.</summary>
  private static string Relative(string path, RunSourceViewModel source) =>
    path.StartsWith(source.Path + " / ", StringComparison.Ordinal) ? path[(source.Path.Length + 3)..] : path == source.Path ? source.Name : path;

  private void ChooseDefaults() {
    var quality = NumericColumns.FirstOrDefault(c => c.Key == "result:BestQuality") ?? NumericColumns.FirstOrDefault(c => c.Key.StartsWith("result:", StringComparison.Ordinal));
    if (ChartValue == null || !NumericColumns.Contains(ChartValue)) ChartValue = quality;
    if (StatisticsValue == null || !NumericColumns.Contains(StatisticsValue)) StatisticsValue = quality;
    XChoices = [new ColumnChoice(RunCharts.RunIndex, "Run (number)"), .. NumericColumns];
    if (ChartX == null || !XChoices.Contains(ChartX)) ChartX = XChoices[0];
    var curves = Table.Curves();
    CurveTables = curves.Select(c => c.Table).ToList();
    if (CurveTable == null || !CurveTables.Contains(CurveTable)) CurveTable = CurveTables.FirstOrDefault(t => t == "Qualities") ?? CurveTables.FirstOrDefault();
  }

  // ---- table

  /// <summary>Shown columns: where runs come from, parameters that differ between runs, and all results.</summary>
  [ObservableProperty]
  public partial IReadOnlyList<ColumnChoice> TableColumns { get; set; } = [];

  [ObservableProperty]
  public partial IReadOnlyList<AnalysisRow> Rows { get; set; } = [];

  [ObservableProperty]
  public partial AnalysisRow? SelectedRow { get; set; }

  [ObservableProperty]
  public partial string? SortKey { get; set; }

  [ObservableProperty]
  public partial bool SortDescending { get; set; }

  [ObservableProperty]
  public partial bool ShowAllParameters { get; set; }

  partial void OnShowAllParametersChanged(bool value) => RebuildRows();

  [RelayCommand]
  private void Sort(string key) {
    if (SortKey == key) SortDescending = !SortDescending;
    else { SortKey = key; SortDescending = false; }
    RebuildRows();
  }

  private void RebuildRows() {
    var varying = Table.Columns.Where(c => c.Kind == "Parameter" && Table.Rows.Select(r => r.Text(c.Key)).Distinct().Skip(1).Any()).Select(c => c.Key).ToHashSet();
    TableColumns = Table.Columns.Where(c => c.Kind is "Run" or "Label" or "Result" || ShowAllParameters || varying.Contains(c.Key))
      .Select(c => new ColumnChoice(c.Key, c.Name)).ToList();
    IEnumerable<RunRow> rows = Table.Rows;
    if (SortKey != null) {
      var numeric = Table.Column(SortKey)?.IsNumeric == true;
      rows = numeric
        ? (SortDescending ? rows.OrderByDescending(r => r.Number(SortKey) ?? double.MinValue) : rows.OrderBy(r => r.Number(SortKey) ?? double.MaxValue))
        : (SortDescending ? rows.OrderByDescending(r => r.Text(SortKey), StringComparer.OrdinalIgnoreCase) : rows.OrderBy(r => r.Text(SortKey), StringComparer.OrdinalIgnoreCase));
    }
    var selected = SelectedRow?.Row;
    Rows = rows.Select(r => new AnalysisRow(r, TableColumns.Select(c => r.Text(c.Key)).ToList())).ToList();
    SelectedRow = Rows.FirstOrDefault(r => ReferenceEquals(r.Row, selected));
  }

  [RelayCommand]
  private async Task ExportAsync() {
    if (fileDialogs == null || await fileDialogs.SaveCsvFileAsync("runs.csv") is not string path) return;
    Export(path);
  }

  public void Export(string path) {
    using var writer = new StreamWriter(path);
    var columns = TableColumns.Select(c => c.Key).ToList();
    // the table in its shown order and with its shown columns
    new RunTable(Rows.Select(r => r.Row).ToList()).WriteCsv(writer, columns);
    Status = $"Exported {Rows.Count} runs to {path}";
  }

  // ---- selected run

  [ObservableProperty]
  public partial IReadOnlyList<NameValue> RunDetails { get; set; } = [];

  [ObservableProperty]
  public partial IReadOnlyList<ResultEntryViewModel> RunResults { get; set; } = [];

  [ObservableProperty]
  public partial ResultEntryViewModel? SelectedRunResult { get; set; }

  [ObservableProperty]
  public partial ResultDetailViewModel? RunResultDetail { get; set; }

  partial void OnSelectedRowChanged(AnalysisRow? value) {
    RunDetails = value == null ? [] : TableColumns.Select(c => new NameValue(c.Display, value.Row.Text(c.Key))).ToList();
    RunResults = value?.Row.Run is { } run
      ? run.Results.Select(r => new ResultEntryViewModel(r.Key, r.Value, ItemInspector.Summary(r.Value))).ToList()
      : [];
    SelectedRunResult = RunResults.FirstOrDefault(r => HeuristicLab.Next.Runtime.Visuals.Visualizations.For(r.Value, r.Name).Count > 0 && r.Name.StartsWith("Best", StringComparison.Ordinal))
                        ?? RunResults.FirstOrDefault();
  }

  partial void OnSelectedRunResultChanged(ResultEntryViewModel? value) =>
    RunResultDetail = value == null ? null : new ResultDetailViewModel(value.Name, value.Value, new EditContext(_ => { }, () => false));

  // ---- charts

  public IReadOnlyList<string> ChartKinds { get; } = ["Box plot", "Scatter", "Histogram", "Cumulative distribution", "Curves"];

  [ObservableProperty]
  public partial string ChartKind { get; set; } = "Box plot";

  [ObservableProperty]
  public partial ColumnChoice? ChartValue { get; set; }

  [ObservableProperty]
  public partial IReadOnlyList<ColumnChoice> XChoices { get; set; } = [];

  [ObservableProperty]
  public partial ColumnChoice? ChartX { get; set; }

  [ObservableProperty]
  public partial IReadOnlyList<string> CurveTables { get; set; } = [];

  [ObservableProperty]
  public partial string? CurveTable { get; set; }

  [ObservableProperty]
  public partial IReadOnlyList<string> CurveRows { get; set; } = [];

  [ObservableProperty]
  public partial string? CurveRow { get; set; }

  [ObservableProperty]
  public partial bool CurveRuns { get; set; } = true;

  [ObservableProperty]
  public partial bool CurveMean { get; set; } = true;

  public bool IsScatter => ChartKind == "Scatter";
  public bool IsCurves => ChartKind == "Curves";
  public bool NeedsValue => !IsCurves;

  public VisualsViewModel Chart { get; } = new();

  partial void OnChartKindChanged(string value) {
    OnPropertyChanged(nameof(IsScatter));
    OnPropertyChanged(nameof(IsCurves));
    OnPropertyChanged(nameof(NeedsValue));
    RebuildChart();
  }
  partial void OnChartValueChanged(ColumnChoice? value) => RebuildChart();
  partial void OnChartXChanged(ColumnChoice? value) => RebuildChart();
  partial void OnCurveTableChanged(string? value) {
    CurveRows = Table.Curves().FirstOrDefault(c => c.Table == value).Rows ?? [];
    if (CurveRow == null || !CurveRows.Contains(CurveRow)) CurveRow = CurveRows.FirstOrDefault(r => r == "BestQuality") ?? CurveRows.FirstOrDefault();
    RebuildChart();
  }
  partial void OnCurveRowChanged(string? value) => RebuildChart();
  partial void OnCurveRunsChanged(bool value) => RebuildChart();
  partial void OnCurveMeanChanged(bool value) => RebuildChart();

  private void RebuildChart() {
    if (Groups.Count == 0) { Chart.Show([]); return; }
    HeuristicLab.Next.Runtime.Visuals.Visual? visual = ChartKind switch {
      "Curves" when CurveTable != null && CurveRow != null => RunCharts.Curves(Groups, CurveTable, CurveRow, CurveRuns, CurveMean),
      "Curves" => new HeuristicLab.Next.Runtime.Visuals.TextVisual("Curves", "The ticked runs have no tables (results folders keep only numbers)."),
      _ when ChartValue == null => null,
      "Scatter" when ChartX != null => RunCharts.Scatter(Table, Groups, ChartX.Key, ChartValue.Key),
      "Histogram" => RunCharts.Histogram(Table, Groups, ChartValue.Key),
      "Cumulative distribution" => RunCharts.Cumulative(Table, Groups, ChartValue.Key),
      _ => RunCharts.BoxPlot(Table, Groups, ChartValue.Key)
    };
    Chart.Show(visual == null ? [] : [visual]);
  }

  // ---- statistics

  [ObservableProperty]
  public partial ColumnChoice? StatisticsValue { get; set; }

  [ObservableProperty]
  public partial IReadOnlyList<SummaryLine> Summaries { get; set; } = [];

  [ObservableProperty]
  public partial IReadOnlyList<PairLine> Pairs { get; set; } = [];

  [ObservableProperty]
  public partial string AllGroupsTest { get; set; } = "";

  partial void OnStatisticsValueChanged(ColumnChoice? value) => RebuildStatistics();

  private static string G(double v) => double.IsFinite(v) ? v.ToString("G6", Invariant) : "–";
  private static string P(double p) => double.IsFinite(p) ? (p < 0.0001 ? "< 0.0001" : p.ToString("0.0000", Invariant)) : "–";

  private void RebuildStatistics() {
    if (StatisticsValue == null || Groups.Count == 0) { Summaries = []; Pairs = []; AllGroupsTest = ""; return; }
    var comparison = RunStatistics.Compare(Groups, StatisticsValue.Key);
    Summaries = comparison.Groups.Select(g => new SummaryLine(g.Group, g.Count.ToString(Invariant), G(g.Mean), G(g.StdDev), G(g.Min), G(g.Q1), G(g.Median), G(g.Q3), G(g.Max))).ToList();
    Pairs = comparison.Pairs.Select(p => new PairLine($"{p.A}  vs  {p.B}", P(p.MannWhitneyP), P(p.AdjustedP), P(p.TTestP), G(p.CohensD), G(p.HedgesG), p.AdjustedP < 0.05)).ToList();
    AllGroupsTest = comparison.KruskalWallisP is double kw
      ? $"Kruskal-Wallis over all {comparison.Groups.Count} groups: p = {P(kw)}" + (kw < 0.05 ? " - the groups differ (α = 0.05)." : " - no significant difference (α = 0.05).")
      : "Tests need at least two groups with two or more runs each.";
  }
}
