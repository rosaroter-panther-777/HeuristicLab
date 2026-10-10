using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Next.Runtime.Tracing;
using HeuristicLab.Next.Runtime.Visuals;
using HeuristicLab.Optimization;
using HeuristicLab.Studio.Controls;

namespace HeuristicLab.Studio.ViewModels;

/// <summary>
/// "Detailed analysis" of an algorithm: a run of a copy of it that records every solution created
/// (DetailedRun), then lets the user walk through it. The chart shows the qualities per iteration
/// (time along the top); clicking selects an iteration, shift+clicking a range. The picture above
/// shows the solution at the current step, with the solutions of earlier iterations (the range,
/// or the last few) fading with their age, and the current solution's parents.
/// </summary>
public partial class AnalysisViewModel : ViewModelBase {
  private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
  private readonly Func<IAlgorithm> algorithm;
  private readonly Action<bool> setRunning;
  private CancellationTokenSource? stop;
  private IReadOnlyList<TracedIteration> iterations = [];

  public AnalysisViewModel(Func<IAlgorithm> algorithm, Action<bool> setRunning) {
    this.algorithm = algorithm;
    this.setRunning = setRunning;
  }

  public string? Unsupported => DetailedRun.WhyNot(algorithm());
  public bool IsSupported => Unsupported == null;

  [ObservableProperty]
  public partial Trace? Trace { get; set; }

  public bool HasTrace => Trace != null;

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
  public partial bool IsRecording { get; set; }

  [ObservableProperty]
  public partial string StorageLimitText { get; set; } = "200000";

  [ObservableProperty]
  public partial string Status { get; set; } = "";

  /// <summary>How many earlier iterations are drawn (fading) when no range is selected.</summary>
  [ObservableProperty]
  public partial decimal Trail { get; set; } = 5;

  [ObservableProperty]
  public partial bool ShowParents { get; set; } = true;

  // ---- chart

  [ObservableProperty]
  public partial IReadOnlyList<ChartSeries> Series { get; set; } = [];

  [ObservableProperty]
  public partial IReadOnlyList<ChartMarker> TimeAxis { get; set; } = [];

  [ObservableProperty]
  public partial IReadOnlyList<ChartMarker> Markers { get; set; } = [];

  [ObservableProperty]
  public partial double? SelectionStart { get; set; }

  [ObservableProperty]
  public partial double? SelectionEnd { get; set; }

  // ---- cursor

  [ObservableProperty]
  public partial int CurrentIteration { get; set; }

  /// <summary>Index of the current step within the current iteration.</summary>
  [ObservableProperty]
  public partial int CurrentStep { get; set; }

  [ObservableProperty]
  public partial string IterationText { get; set; } = "";

  [ObservableProperty]
  public partial string StepText { get; set; } = "";

  [ObservableProperty]
  public partial string RangeText { get; set; } = "";

  public VisualsViewModel Visuals { get; } = new();

  partial void OnTraceChanged(Trace? value) => OnPropertyChanged(nameof(HasTrace));
  partial void OnTrailChanged(decimal value) => ShowCursor();
  partial void OnShowParentsChanged(bool value) => ShowCursor();

  private bool syncing;

  partial void OnSelectionStartChanged(double? value) {
    if (syncing || value is not double x) return;
    GoTo((int)Math.Round(x), best: true);
  }

  partial void OnSelectionEndChanged(double? value) {
    if (syncing) return;
    if (value is double end && SelectionStart is double start) GoTo((int)Math.Round(Math.Min(start, end)), best: true);
    ShowCursor();
  }

  private (int From, int To)? Range =>
    SelectionStart is double a && SelectionEnd is double b ? ((int)Math.Round(Math.Min(a, b)), (int)Math.Round(Math.Max(a, b))) : null;

  // ---- running

  private bool CanStart() => !IsRecording && IsSupported;

  [RelayCommand(CanExecute = nameof(CanStart))]
  public async Task StartAsync() {
    if (!int.TryParse(StorageLimitText, NumberStyles.Integer, Invariant, out var limit) || limit < 0) {
      Status = "Keep: a whole number of solutions.";
      return;
    }
    stop = new CancellationTokenSource();
    var (trace, run) = DetailedRun.Start(algorithm(), new DetailedRunOptions { StorageLimit = limit }, stop.Token);
    Trace = trace;
    iterations = [];
    syncing = true;
    SelectionStart = SelectionEnd = null;
    syncing = false;
    IsRecording = true;
    setRunning(true);
    Status = "Recording ...";
    try {
      var report = await run;
      Refresh();
      Status = report.Outcome switch {
        RunOutcome.Failed => "Failed: " + report.Error?.Split('\n')[0],
        _ => $"{(report.Outcome == RunOutcome.Stopped ? "Stopped" : "Finished")}: {trace.SolutionCount:N0} solutions in {iterations.Count:N0} iterations"
             + (trace.SolutionCount > trace.StorageLimit ? $" (the first {trace.StorageLimit:N0} kept for drawing)" : "") + "."
      };
    } finally {
      IsRecording = false;
      setRunning(false);
      stop.Dispose();
      stop = null;
    }
  }

  private bool CanStop() => IsRecording;

  [RelayCommand(CanExecute = nameof(CanStop))]
  private void Stop() => stop?.Cancel();

  /// <summary>Called by the live timer while recording, and once at the end.</summary>
  public void Refresh() {
    if (Trace is not { } trace) return;
    iterations = trace.Iterations();
    if (iterations.Count == 0) return;
    Series = [
      Line("Best so far", 3, iterations.Where(i => i.BestSoFar.HasValue).Select(i => new ChartPoint(i.Number, i.BestSoFar!.Value))),
      Line("Best", 0, iterations.Where(i => i.Best.HasValue).Select(i => new ChartPoint(i.Number, i.Best!.Value))),
      Line("Average", 1, iterations.Where(i => i.Average.HasValue).Select(i => new ChartPoint(i.Number, i.Average!.Value))),
      Line("Worst", 2, iterations.Where(i => i.Worst.HasValue).Select(i => new ChartPoint(i.Number, i.Worst!.Value)))
    ];
    TimeAxis = TimeTicks(iterations);
    // while recording, follow the newest iteration until the user selects one
    if (SelectionStart == null) GoTo(iterations[^1].Number, best: true);
    else UpdateTexts();
  }

  private static ChartSeries Line(string name, int color, IEnumerable<ChartPoint> points) =>
    new(name, ChartSeries.PaletteColor(color), points.ToList());

  /// <summary>Elapsed time at about six iterations, for the top axis.</summary>
  private static IReadOnlyList<ChartMarker> TimeTicks(IReadOnlyList<TracedIteration> all) {
    int step = Math.Max(1, (all.Count - 1) / 6);
    return Enumerable.Range(0, all.Count).Where(i => i % step == 0).Select(i => all[i])
      .Select(i => new ChartMarker(i.Number, Seconds(i.Start + i.Duration))).ToList();
  }

  private static string Seconds(double s) => s < 10 ? s.ToString("0.00", Invariant) + " s" : s.ToString("0.#", Invariant) + " s";

  // ---- navigation

  private TracedIteration? IterationAt(int number) => iterations.FirstOrDefault(i => i.Number == number);

  private void GoTo(int iteration, bool best) {
    if (Trace is not { } trace || iterations.Count == 0) return;
    var it = IterationAt(iteration) ?? iterations.MinBy(i => Math.Abs(i.Number - iteration))!;
    CurrentIteration = it.Number;
    CurrentStep = best && it.StepCount > 0 && trace.Representative(it.Number) is { } r ? r.Id - it.FirstStep : Math.Max(0, it.StepCount - 1);
    ShowCursor();
  }

  [RelayCommand]
  private void PreviousIteration() => MoveIteration(-1);

  [RelayCommand]
  private void NextIteration() => MoveIteration(+1);

  private void MoveIteration(int delta) {
    int index = iterations.ToList().FindIndex(i => i.Number == CurrentIteration) + delta;
    if (index < 0 || index >= iterations.Count) return;
    GoTo(iterations[index].Number, best: true);
    if (Range == null) { syncing = true; SelectionStart = CurrentIteration; syncing = false; }
  }

  [RelayCommand]
  private void PreviousStep() => MoveStep(-1);

  [RelayCommand]
  private void NextStep() => MoveStep(+1);

  /// <summary>Steps go on into the neighbouring iteration at either end.</summary>
  private void MoveStep(int delta) {
    if (IterationAt(CurrentIteration) is not { } it) return;
    int step = CurrentStep + delta;
    int index = iterations.ToList().IndexOf(it);
    if (step < 0) {
      var previous = iterations.Take(index).LastOrDefault(i => i.StepCount > 0);
      if (previous == null) return;
      CurrentIteration = previous.Number; CurrentStep = previous.StepCount - 1;
    } else if (step >= it.StepCount) {
      var next = iterations.Skip(index + 1).FirstOrDefault(i => i.StepCount > 0);
      if (next == null) return;
      CurrentIteration = next.Number; CurrentStep = 0;
    } else CurrentStep = step;
    if (Range == null) { syncing = true; SelectionStart = CurrentIteration; syncing = false; }
    ShowCursor();
  }

  /// <summary>Draws the current step's solution over the faded solutions of earlier iterations (and its parents).</summary>
  private void ShowCursor() {
    UpdateTexts();
    if (Trace is not { } trace || IterationAt(CurrentIteration) is not { StepCount: > 0 } it) return;
    var current = trace.Solution(it.FirstStep + Math.Clamp(CurrentStep, 0, it.StepCount - 1));
    if (current.Solution == null) {
      Visuals.Show([new TextVisual("Not kept", $"Solution #{current.Id} was recorded after the storage limit ({trace.StorageLimit:N0}); raise \"Keep\" to draw it.")]);
      return;
    }
    try {
      var pictures = Visualizations.ForSolution(trace.Problem, current.Solution, current.Quality).ToList();
      var faded = new List<(Visual, double)>();
      var window = EarlierIterations();
      for (int i = 0; i < window.Count; i++) {
        double opacity = 0.55 * (i + 1) / (window.Count + 1);  // oldest faintest
        if (trace.Representative(window[i]) is { Solution: { } s } r && r.Id != current.Id)
          faded.Add((Visualizations.ForSolution(trace.Problem, s, r.Quality)[0], opacity));
      }
      if (ShowParents)
        foreach (var parent in current.Parents.Select(trace.Solution).Where(p => p.Solution != null))
          faded.Add((Visualizations.ForSolution(trace.Problem, parent.Solution!, parent.Quality)[0], 0.45));
      pictures[0] = Visualizations.Overlay(pictures[0], faded) with { Title = pictures[0].Title };
      Visuals.Show(pictures);
    } catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException or IndexOutOfRangeException) {
      Visuals.Show([new TextVisual("Not drawn", e.Message)]);
    }
  }

  /// <summary>Iterations drawn faded: the selected range up to the current one, else the last few before it.</summary>
  private IReadOnlyList<int> EarlierIterations() {
    var before = iterations.Where(i => i.Number < CurrentIteration).Select(i => i.Number).ToList();
    if (Range is var (from, _)) return before.Where(n => n >= from).ToList();
    return before.TakeLast((int)Trail).ToList();
  }

  private void UpdateTexts() {
    if (Trace is not { } trace || IterationAt(CurrentIteration) is not { } it) {
      IterationText = StepText = RangeText = "";
      Markers = [];
      return;
    }
    string Q(double? q) => q?.ToString("G8", Invariant) ?? "–";
    IterationText = $"Iteration {it.Number} of {iterations[^1].Number} · {Seconds(it.Duration)} · {it.StepCount:N0} solutions created · best {Q(it.Best)} · best so far {Q(it.BestSoFar)}";
    if (it.StepCount > 0) {
      var s = trace.Solution(it.FirstStep + Math.Clamp(CurrentStep, 0, it.StepCount - 1));
      string from = s.Parents.Count == 0 ? "" : $" from {string.Join(", ", s.Parents.Select(p => "#" + p))}";
      StepText = $"Step {CurrentStep + 1} of {it.StepCount}: {s.Operator} created #{s.Id}{from} · quality {Q(s.Quality)} · at {Seconds(s.Seconds)}";
    } else StepText = "No solution was created in this iteration.";
    if (Range is var (a, b)) {
      var inRange = iterations.Where(i => i.Number >= a && i.Number <= b).ToList();
      var first = inRange.FirstOrDefault();
      var last = inRange.LastOrDefault();
      double seconds = inRange.Sum(i => i.Duration);
      string change = first?.BestSoFar is double q0 && last?.BestSoFar is double q1 ? $" · best so far {Q(q0)} → {Q(q1)} ({(q1 - q0).ToString("+0.###;-0.###;0", Invariant)})" : "";
      RangeText = $"Iterations {a}–{b}: {inRange.Sum(i => i.StepCount):N0} solutions in {Seconds(seconds)}{change}";
      Markers = [new ChartMarker(CurrentIteration, "current")];
    } else {
      RangeText = "";
      Markers = [];
    }
  }
}
