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
  private readonly List<RunProgress> history = [];
  private IOptimizer? optimizer;
  private string? filePath;

  public MainViewModel() : this(null) { }

  public MainViewModel(IFileDialogService? fileDialogs, IDialogService? dialogs = null) {
    this.fileDialogs = fileDialogs;
    this.dialogs = dialogs;
    ResultsBrowser = new ResultsBrowserViewModel(fileDialogs);
  }

  public ResultsBrowserViewModel ResultsBrowser { get; }
  public SolutionViewModel Solution { get; } = new();

  [ObservableProperty]
  public partial string RepetitionsText { get; set; } = "1";

  [ObservableProperty]
  public partial string ParallelText { get; set; } = "1";

  [ObservableProperty]
  public partial string? ResultsFolder { get; set; }

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
      optimizer = loaded;
      filePath = path;
      HasDocument = true;
      Title = $"{loaded.Name} - HeuristicLab Studio";
      ShowDocument(loaded);
      ClearRunView();
      Solution.Show(loaded);
      Status = $"Loaded {Path.GetFileName(path)}";
    } catch (Exception e) {
      Status = $"Could not load {Path.GetFileName(path)}: {e.Message}";
    }
  }

  [RelayCommand(CanExecute = nameof(CanSave))]
  private async Task SaveAsync() {
    if (optimizer == null || fileDialogs == null) return;
    var path = await fileDialogs.SaveHlFileAsync(Path.GetFileName(filePath) ?? "optimizer.hl");
    if (path == null) return;
    await Task.Run(() => Documents.Save((IStorableContent)optimizer, path));
    filePath = path;
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
  private bool CanRun() => HasDocument && !IsRunning;

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
