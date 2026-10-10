using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeuristicLab.Common;
using HeuristicLab.Core;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Optimization;
using HeuristicLab.Studio.Controls;
using HeuristicLab.Studio.Services;

namespace HeuristicLab.Studio.ViewModels;

public sealed record NameValue(string Name, string Value);

/// <summary>
/// Open a .hl file, inspect it, run it with live progress, save it with its runs.
/// All HeuristicLab access goes through HeuristicLab.Next.Runtime.
/// </summary>
public partial class MainViewModel : ViewModelBase {
  // results plotted when present, in this order (first three are the usual quality curves)
  private static readonly string[] ChartKeys = ["BestQuality", "CurrentBestQuality", "CurrentAverageQuality", "CurrentWorstQuality"];

  private readonly IFileDialogService? fileDialogs;
  private readonly IDialogService? dialogs;
  private readonly ISettingsStore? settingsStore;
  private const int MaxRecentFiles = 10;
  private readonly List<RunProgress> history = [];
  private IOptimizer? optimizer;
  private string? filePath;

  public MainViewModel() : this(null) { }

  public MainViewModel(IFileDialogService? fileDialogs, IDialogService? dialogs = null, ISettingsStore? settingsStore = null) {
    this.fileDialogs = fileDialogs;
    this.dialogs = dialogs;
    this.settingsStore = settingsStore;
    ResultsBrowser = new ResultsBrowserViewModel(fileDialogs);
    Workspace = new ExperimentWorkspaceViewModel(dialogs, fileDialogs, () => ResultsFolder, () => IsRunning, (o, path) => {
      ShowOptimizer(o, path);
      SelectedTab = RunTab;
    }, script => {
      Scripts.Open(script);
      SelectedTab = ScriptsTab;
    });
    Workspace.PropertyChanged += (_, e) => {
      if (e.PropertyName == nameof(ExperimentWorkspaceViewModel.IsRunning)) {
        RunCommand.NotifyCanExecuteChanged();
        // finished experiments: refresh the Run tab if it shows one of them
        if (!Workspace.IsRunning && optimizer != null) { ShowDocument(optimizer); Solution.Show(optimizer); }
      }
    };
    if (settingsStore?.Load() is StudioSettings settings) {
      foreach (var file in settings.RecentFiles.Where(File.Exists).Take(MaxRecentFiles)) RecentFiles.Add(file);
      SeedText = settings.Seed;
      RepetitionsText = settings.Runs;
      ParallelText = settings.Parallel;
      ResultsFolder = settings.ResultsFolder;
    }
  }

  /// <summary>Most recently opened or saved files, newest first.</summary>
  public ObservableCollection<string> RecentFiles { get; } = [];

  [ObservableProperty]
  public partial string? SelectedRecentFile { get; set; }

  partial void OnSelectedRecentFileChanged(string? value) {
    if (value == null || IsRunning) return;
    SelectedRecentFile = null;  // the picker acts like a menu
    _ = LoadAsync(value);
  }

  /// <summary>Stores recent files and run settings (called on exit and after open/save).</summary>
  public void SaveSettings() => settingsStore?.Save(new StudioSettings {
    RecentFiles = RecentFiles.ToList(), Seed = SeedText, Runs = RepetitionsText, Parallel = ParallelText, ResultsFolder = ResultsFolder
  });

  private void Remember(string path) {
    var full = Path.GetFullPath(path);
    RecentFiles.Remove(full);
    RecentFiles.Insert(0, full);
    while (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveAt(RecentFiles.Count - 1);
    SaveSettings();
  }

  public ResultsBrowserViewModel ResultsBrowser { get; }
  /// <summary>Experiments tab: building blocks of several experiments.</summary>
  public ExperimentWorkspaceViewModel Workspace { get; }

  public const int RunTab = 1;
  public const int ScriptsTab = 4;

  /// <summary>C# scripts (HeuristicLab's script samples, or new ones).</summary>
  public ScriptsViewModel Scripts { get; } = new();

  [ObservableProperty]
  public partial int SelectedTab { get; set; }
  public SolutionViewModel Solution { get; } = new();

  [ObservableProperty]
  public partial string RepetitionsText { get; set; } = "1";

  [ObservableProperty]
  public partial string ParallelText { get; set; } = "1";

  [ObservableProperty]
  public partial string? ResultsFolder { get; set; }

  /// <summary>Sweep specs separated by ';', e.g. "PopulationSize=50,200; Selector=TournamentSelector,ProportionalSelector".</summary>
  [ObservableProperty]
  public partial string SweepText { get; set; } = "";

  [ObservableProperty]
  public partial string WalkTrainText { get; set; } = "";

  [ObservableProperty]
  public partial string WalkTestText { get; set; } = "";

  [ObservableProperty]
  public partial string WalkStepText { get; set; } = "";

  [ObservableProperty]
  public partial bool WalkExpanding { get; set; }

  /// <summary>Batch report of the last batch run (null before the first batch).</summary>
  public BatchReport? LastBatch { get; private set; }

  [ObservableProperty]
  public partial string Title { get; set; } = "HeuristicLab Studio";

  [ObservableProperty]
  public partial string DocumentName { get; set; } = "No file open";

  [ObservableProperty]
  public partial string DocumentDetails { get; set; } = "Open a .hl file to inspect and run it.";

  [ObservableProperty]
  public partial string SeedText { get; set; } = "";

  [ObservableProperty]
  public partial string Status { get; set; } = "Ready";

  [ObservableProperty]
  public partial IReadOnlyList<ChartSeries> Series { get; set; } = [];

  [ObservableProperty]
  public partial string ChartXAxisTitle { get; set; } = LiveAxisTitle;

  private const string LiveAxisTitle = "Elapsed time [s]";

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(SaveCommand), nameof(OpenCommand), nameof(NewCommand))]
  public partial bool IsRunning { get; set; }

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(SaveCommand))]
  public partial bool HasDocument { get; set; }

  public ObservableCollection<ParameterRowViewModel> Parameters { get; } = [];
  public ObservableCollection<NameValue> Results { get; } = [];

  /// <summary>Report of the last run (null before the first run).</summary>
  public RunReport? LastReport { get; private set; }

  [RelayCommand(CanExecute = nameof(CanOpen))]
  private async Task NewAsync() {
    if (dialogs != null && await dialogs.NewSetupAsync() is SetupResult setup) ShowNew(setup);
  }

  /// <summary>Shows a freshly created (unsaved) setup as the current document.</summary>
  public void ShowNew(SetupResult setup) {
    optimizer = setup.Algorithm;
    filePath = null;
    HasDocument = true;
    Title = $"{setup.Algorithm.Name} - HeuristicLab Studio";
    ShowDocument(setup.Algorithm);
    ClearRunView();
    Solution.Show(setup.Algorithm);
    Status = setup.Messages.Count > 0 ? string.Join("; ", setup.Messages) : $"Created {setup.Algorithm.Name}";
  }

  [RelayCommand]
  private async Task ChooseResultsFolderAsync() {
    if (fileDialogs != null && await fileDialogs.PickFolderAsync("Results folder") is string folder) ResultsFolder = folder;
  }

  [RelayCommand(CanExecute = nameof(CanOpen))]
  private async Task OpenAsync() {
    if (fileDialogs == null) return;
    var path = await fileDialogs.OpenHlFileAsync();
    if (path != null) await LoadAsync(path);
  }
  private bool CanOpen() => !IsRunning;

  public async Task LoadAsync(string path) {
    Status = $"Loading {Path.GetFileName(path)} ...";
    try {
      var content = await Task.Run(() => Documents.Load(path));
      if (content is not IOptimizer loaded) {
        Status = $"{Path.GetFileName(path)} does not contain an algorithm, experiment or batch run.";
        return;
      }
      ShowOptimizer(loaded, path);
      Remember(path);
      Status = $"Loaded {Path.GetFileName(path)}";
    } catch (Exception e) {
      Status = $"Could not load {Path.GetFileName(path)}: {e.Message}";
    }
  }

  /// <summary>Makes an optimizer (opened, or from the Experiments tab) the Run tab's document.</summary>
  public void ShowOptimizer(IOptimizer loaded, string? path) {
    optimizer = loaded;
    filePath = path;
    HasDocument = true;
    Title = $"{loaded.Name} - HeuristicLab Studio";
    ShowDocument(loaded);
    ClearRunView();
    Solution.Show(loaded);
    Status = $"Showing {loaded.Name}";
  }

  partial void OnIsRunningChanged(bool value) => Workspace.RefreshState();

  [RelayCommand(CanExecute = nameof(CanSave))]
  private async Task SaveAsync() {
    if (optimizer == null || fileDialogs == null) return;
    var path = await fileDialogs.SaveHlFileAsync(Path.GetFileName(filePath) ?? "optimizer.hl");
    if (path == null) return;
    await Task.Run(() => Documents.Save((IStorableContent)optimizer, path));
    filePath = path;
    Remember(path);
    Status = $"Saved {Path.GetFileName(path)} ({optimizer.Runs.Count} runs)";
  }
  private bool CanSave() => HasDocument && !IsRunning;

  [RelayCommand(CanExecute = nameof(CanRun), IncludeCancelCommand = true)]
  private async Task RunAsync(CancellationToken cancellationToken) {
    if (optimizer == null) return;
    int? seed = null;
    if (!string.IsNullOrWhiteSpace(SeedText)) {
      if (!int.TryParse(SeedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) {
        Status = $"Seed must be a whole number, not '{SeedText}'.";
        return;
      }
      seed = parsed;
    }

    if (!int.TryParse(RepetitionsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var repetitions) || repetitions < 1
        || !int.TryParse(ParallelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parallel) || parallel < 1) {
      Status = "Runs and parallel must be whole numbers of at least 1.";
      return;
    }
    if (!string.IsNullOrWhiteSpace(WalkTrainText)) {
      await RunWalkForwardAsync(parallel, seed, cancellationToken);
      return;
    }
    if (!string.IsNullOrWhiteSpace(SweepText)) {
      await RunSweepAsync(repetitions, parallel, seed, cancellationToken);
      return;
    }
    if (repetitions > 1) {
      await RunBatchAsync(repetitions, parallel, seed, cancellationToken);
      return;
    }

    IsRunning = true;
    ClearRunView();
    Status = seed is int s ? $"Running with seed {s} ..." : "Running ...";
    try {
      // Progress<T> captures the UI synchronization context: updates arrive on the UI thread
      var progress = new Progress<RunProgress>(OnProgress);
      var options = new RunOptions { Seed = seed, ProgressInterval = TimeSpan.FromMilliseconds(200) };
      var report = await OptimizerRunner.RunAsync(optimizer, options, filePath, progress, cancellationToken);
      LastReport = report;
      if (ResultsFolder != null) new ResultStore(ResultsFolder).Add(report);
      ShowReport(report);
      ShowQualityTable(report);
      ShowDocument(optimizer);
      Solution.Show(optimizer);
    } finally {
      IsRunning = false;
    }
  }
  private bool CanRun() => HasDocument && !IsRunning && !Workspace.IsRunning;

  private void OnProgress(RunProgress progress) {
    if (!IsRunning) return;  // Progress<T> delivers asynchronously; ignore anything after the run ended
    history.Add(progress);
    Series = BuildSeries(history);
    Results.Clear();
    foreach (var (name, value) in progress.Values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
      Results.Add(new NameValue(name, Format(value)));
    Status = $"Running ... {progress.Elapsed:hh\\:mm\\:ss}";
  }

  private void ShowReport(RunReport report) {
    var run = report.Runs.LastOrDefault();
    if (run != null) {
      Results.Clear();
      foreach (var (name, value) in run.Results.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
        if (value.Value is double or int or long or bool or string)
          Results.Add(new NameValue(name, value.Value is double d ? Format(d) : Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? ""));
    }
    var time = TimeSpan.FromSeconds(report.ExecutionSeconds);
    Status = report.Outcome switch {
      RunOutcome.Completed => $"Completed after {time:hh\\:mm\\:ss\\.f}",
      RunOutcome.Stopped => $"Stopped after {time:hh\\:mm\\:ss\\.f}",
      _ => $"Failed: {report.Error?.Split('\n')[0]}"
    };
  }

  private async Task RunBatchAsync(int repetitions, int parallel, int? seed, CancellationToken cancellationToken) {
    IsRunning = true;
    ClearRunView();
    ChartXAxisTitle = "Run";
    var store = ResultsFolder == null ? null : new ResultStore(ResultsFolder);
    int done = 0;
    var best = new List<ChartPoint>();
    Status = $"Running {repetitions} runs ...";
    try {
      var batch = await BatchRunner.RepeatAsync(BatchRunner.Copies(optimizer!), repetitions, seed, parallel,
        new RunOptions(), filePath, (i, report) => {
          store?.Add(report);
          var value = KeyMetric(report);
          // called from worker threads: hand the UI update to the UI thread
          Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            done++;
            if (value is double v) {
              best.Add(new ChartPoint(i + 1, v));
              Series = [new ChartSeries((KeyMetricName(report) ?? "Result") + " per run", ChartSeries.PaletteColor(0),
                                        best.OrderBy(p => p.X).ToList(), PointsOnly: true)];
            }
            Status = $"Run {done} of {repetitions} finished";
          });
        }, cancellationToken);
      LastBatch = batch;
      await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });  // flush posted progress first
      Results.Clear();
      foreach (var (name, s) in batch.Summary)
        Results.Add(new NameValue(name, $"mean {Format(s.Mean)}  sd {Format(s.StdDev)}  [{Format(s.Min)} .. {Format(s.Max)}]  n={s.Count}"));
      Status = $"{batch.Completed} of {repetitions} runs completed (seeds {batch.BaseSeed}..{batch.BaseSeed + repetitions - 1})" +
               (store != null ? $", stored in {store.Directory}" : "");
      if (store != null && ResultsBrowser.Folder == store.Directory) ResultsBrowser.Load(store.Directory);
    } finally {
      IsRunning = false;
    }
  }

  private async Task RunSweepAsync(int repetitions, int parallel, int? seed, CancellationToken cancellationToken) {
    var specs = SweepText.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    IsRunning = true;
    ClearRunView();
    var store = ResultsFolder == null ? null : new ResultStore(ResultsFolder);
    int done = 0, total = Sweeps.Configurations(specs).Count * repetitions;
    Status = $"Sweep: {total} runs ...";
    try {
      var results = await Sweeps.RunAsync(optimizer!, specs, repetitions, seed, parallel, new RunOptions(), filePath,
        (config, i, report) => {
          store?.Add(report);
          Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = $"Sweep: {++done} of {total} runs finished");
        }, cancellationToken);
      await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
      var metric = KeyMetricNames.FirstOrDefault(k => results.Any(r => r.Batch.Summary.ContainsKey(k)));
      Results.Clear();
      var points = new List<ChartPoint>();
      for (int c = 0; c < results.Count; c++) {
        var r = results[c];
        if (metric != null && r.Batch.Summary.TryGetValue(metric, out var stats)) {
          Results.Add(new NameValue(r.Configuration, $"mean {Format(stats.Mean)}  sd {Format(stats.StdDev)}  [{Format(stats.Min)} .. {Format(stats.Max)}]  n={stats.Count}"));
          points.AddRange(r.Batch.Reports.Select(KeyMetric).OfType<double>().Select(v => new ChartPoint(c + 1, v)));
        }
      }
      if (points.Count > 0) Series = [new ChartSeries($"{metric} per run, by configuration", ChartSeries.PaletteColor(0), points, PointsOnly: true)];
      ChartXAxisTitle = "Configuration (in the order listed below)";
      Status = $"Sweep finished: {results.Count} configurations x {repetitions} runs" + (store != null ? $", stored in {store.Directory}" : "");
      if (store != null && ResultsBrowser.Folder == store.Directory) ResultsBrowser.Load(store.Directory);
    } catch (ArgumentException e) {
      Status = e.Message;
    } finally {
      IsRunning = false;
    }
  }

  private async Task RunWalkForwardAsync(int parallel, int? seed, CancellationToken cancellationToken) {
    if (optimizer is not IAlgorithm algorithm) { Status = "Walk-forward validation needs an algorithm."; return; }
    if (!int.TryParse(WalkTrainText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var train)
        || !int.TryParse(WalkTestText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var test)
        || (WalkStepText.Length > 0 && !int.TryParse(WalkStepText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))) {
      Status = "Walk-forward: training rows, test rows (and step) must be whole numbers.";
      return;
    }
    int? step = WalkStepText.Length > 0 ? int.Parse(WalkStepText, CultureInfo.InvariantCulture) : null;
    IsRunning = true;
    ClearRunView();
    var store = ResultsFolder == null ? null : new ResultStore(ResultsFolder);
    Status = "Walk-forward ...";
    try {
      var walk = await WalkForward.RunAsync(algorithm, new WalkForwardOptions(train, test) { Step = step, Expanding = WalkExpanding },
        seed, parallel, null, filePath, fold => {
          store?.Add(fold.Report);
          Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = $"Walk-forward: fold {fold.Index} finished");
        }, cancellationToken);
      await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
      Results.Clear();
      foreach (var (name, s) in walk.TestSummary)
        Results.Add(new NameValue(ShortName(name), $"mean {Format(s.Mean)}  sd {Format(s.StdDev)}  [{Format(s.Min)} .. {Format(s.Max)}]  n={s.Count}"));
      // per fold: the first test metric (e.g. Sharpe ratio or R²), placed at the fold's test start
      var key = walk.TestSummary.Keys.FirstOrDefault(k => k.Contains("Sharpe") || k.Contains("R²")) ?? walk.TestSummary.Keys.FirstOrDefault();
      if (key != null) {
        var points = walk.Folds.OrderBy(f => f.Index)
          .Select(f => f.Report.Runs.Single().Results.TryGetValue(key, out var v) && v.Value is double d ? new ChartPoint(f.Test.Start, d) : (ChartPoint?)null)
          .OfType<ChartPoint>().ToList();
        Series = [new ChartSeries(ShortName(key), ChartSeries.PaletteColor(1), points, PointsOnly: true)];
        ChartXAxisTitle = "First test row of the fold";
      }
      Status = $"Walk-forward finished: {walk.Folds.Count} folds" + (store != null ? $", stored in {store.Directory}" : "");
      if (store != null && ResultsBrowser.Folder == store.Directory) ResultsBrowser.Load(store.Directory);
    } catch (ArgumentException e) {
      Status = e.Message;
    } finally {
      IsRunning = false;
    }
  }

  /// <summary>"Best training solution.Sharpe ratio (test)" -> "Sharpe ratio (test)".</summary>
  private static string ShortName(string resultName) => resultName[(resultName.LastIndexOf('.') + 1)..];

  private static readonly string[] KeyMetricNames = ["BestQuality", "CurrentBestQuality"];
  private static string? KeyMetricName(RunReport report) =>
    KeyMetricNames.FirstOrDefault(k => report.Runs.LastOrDefault()?.Results.ContainsKey(k) == true);
  private static double? KeyMetric(RunReport report) =>
    KeyMetricName(report) is string k ? ItemValueNumber(report.Runs.Last().Results[k]) : null;
  private static double? ItemValueNumber(ItemValue value) => value.Value switch { double d => d, int i => i, long l => l, _ => null };

  private void ShowDocument(IOptimizer loaded) {
    DocumentName = loaded.Name;
    var problem = loaded is IAlgorithm { Problem: not null } algorithm ? algorithm.Problem.Name : null;
    DocumentDetails = string.Join(Environment.NewLine, new[] {
      ItemAttribute.GetName(loaded.GetType()),
      problem == null ? null : $"Problem: {problem}",
      $"State: {loaded.ExecutionState}, stored runs: {loaded.Runs.Count}"
    }.Where(l => l != null));
    Parameters.Clear();
    if (loaded is IParameterizedItem parameterized)
      foreach (var parameter in ParameterEditor.Describe(parameterized))
        Parameters.Add(new ParameterRowViewModel(parameterized, parameter, message => Status = message));
  }

  private void ClearRunView() {
    history.Clear();
    Series = [];
    ChartXAxisTitle = LiveAxisTitle;
    Results.Clear();
  }

  /// <summary>
  /// After a run the live curve (sampled every few hundred ms) is replaced by the algorithm's own
  /// quality table, which has one value per generation/iteration.
  /// </summary>
  private void ShowQualityTable(RunReport report) {
    var tables = report.Runs.LastOrDefault()?.Results.Values.Select(v => v.Value).OfType<TableValue>().ToList();
    var table = tables?.FirstOrDefault(t => t.Name == "Qualities")
             ?? tables?.FirstOrDefault(t => t.Rows.Keys.Any(ChartKeys.Contains));
    if (table == null) return;
    var keys = ChartKeys.Where(table.Rows.ContainsKey).ToList();
    if (keys.Contains("BestQuality")) keys.Remove("CurrentBestQuality");
    if (keys.Count == 0) return;
    Series = keys.Select((key, index) => new ChartSeries(key, ChartSeries.PaletteColor(index),
        table.Rows[key].Select((value, i) => new ChartPoint(i, value)).ToList()))
      .ToList();
    ChartXAxisTitle = string.IsNullOrEmpty(table.XAxisTitle) ? "Iterations" : table.XAxisTitle;
  }

  private static IReadOnlyList<ChartSeries> BuildSeries(IReadOnlyList<RunProgress> snapshots) {
    var keys = ChartKeys.Where(k => snapshots.Any(s => s.Values.ContainsKey(k))).ToList();
    // BestQuality and CurrentBestQuality usually coincide; show the overall best only
    if (keys.Contains("BestQuality")) keys.Remove("CurrentBestQuality");
    return keys.Select((key, index) => new ChartSeries(key, ChartSeries.PaletteColor(index),
        snapshots.Where(s => s.Values.ContainsKey(key))
                 .Select(s => new ChartPoint(s.Elapsed.TotalSeconds, s.Values[key])).ToList()))
      .ToList();
  }

  private static string Format(double value) => value.ToString("G8", CultureInfo.InvariantCulture);
}
