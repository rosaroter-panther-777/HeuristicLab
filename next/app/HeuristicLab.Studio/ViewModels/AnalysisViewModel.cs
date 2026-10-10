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
/// shows the solution at the current step in red, what changed in its iteration in yellow, earlier
/// iterations in shades of blue and later ones in shades of green (see Composition); a selected
/// range can be played as an animation.
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

  /// <summary>How many earlier iterations are drawn (blue), within the selected range if there is one.</summary>
  [ObservableProperty]
  public partial decimal EarlierShown { get; set; } = 5;

  /// <summary>How many later iterations are drawn (green) once the run has finished, within the selected range if there is one.</summary>
  [ObservableProperty]
  public partial decimal LaterShown { get; set; } = 5;

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

  /// <summary>Chart series the user has hidden (shared by the normal and the fullscreen view).</summary>
  public HashSet<string> HiddenSeries { get; } = [];

  partial void OnTraceChanged(Trace? value) => OnPropertyChanged(nameof(HasTrace));
  partial void OnEarlierShownChanged(decimal value) => ShowCursor();
  partial void OnLaterShownChanged(decimal value) => ShowCursor();

  private bool syncing;

  partial void OnSelectionStartChanged(double? value) {
    PlayCommand.NotifyCanExecuteChanged();
    if (syncing || value is not double x) return;
    GoTo((int)Math.Round(x), best: true);
  }

  partial void OnSelectionEndChanged(double? value) {
    PlayCommand.NotifyCanExecuteChanged();
    if (value == null) StopAnimation();
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
    // best so far last: on top where the lines coincide (one solution per iteration)
    Series = [
      Line("Worst", 2, iterations.Where(i => i.Worst.HasValue).Select(i => new ChartPoint(i.Number, i.Worst!.Value))),
      Line("Average", 1, iterations.Where(i => i.Average.HasValue).Select(i => new ChartPoint(i.Number, i.Average!.Value))),
      Line("Best", 0, iterations.Where(i => i.Best.HasValue).Select(i => new ChartPoint(i.Number, i.Best!.Value))),
      Line("Best so far", 3, iterations.Where(i => i.BestSoFar.HasValue).Select(i => new ChartPoint(i.Number, i.BestSoFar!.Value)))
    ];
    TimeAxis = TimeTicks(iterations);
    OnPropertyChanged(nameof(HasSteps));
    PlayCommand.NotifyCanExecuteChanged();
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
      Visual? Picture(int iteration) =>
        trace.Representative(iteration) is { Solution: { } solution } r && r.Id != current.Id
          ? Visualizations.ForSolution(trace.Problem, solution, r.Quality)[0] : null;
      var baselines = Ancestors(trace, current).Where(a => a.Solution != null)
        .Select(a => Visualizations.ForSolution(trace.Problem, a.Solution!, a.Quality)[0]).ToList();
      var layers = new List<SolutionLayer>();
      foreach (var (number, kind, age) in Layers())
        if (Picture(number) is { } picture) layers.Add(new SolutionLayer(picture, kind, age));
      pictures[0] = Composition.Compose(pictures[0], baselines, layers) with { Title = pictures[0].Title };
      Visuals.Show(pictures);
    } catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException or IndexOutOfRangeException) {
      Visuals.Show([new TextVisual("Not drawn", e.Message)]);
    }
  }

  /// <summary>
  /// What a solution was made from before its iteration began: its parents, through solutions
  /// created earlier in the same iteration (a mutation of a crossover child goes back to the
  /// crossover's parents). What it has beyond them changed during the iteration.
  /// </summary>
  private static IReadOnlyList<TracedSolution> Ancestors(Trace trace, TracedSolution solution) {
    var result = new List<TracedSolution>();
    var seen = new HashSet<int>();
    var pending = new Stack<int>(solution.Parents);
    while (pending.Count > 0 && result.Count < 16) {
      var parent = trace.Solution(pending.Pop());
      if (!seen.Add(parent.Id)) continue;
      if (parent.Iteration < solution.Iteration) result.Add(parent);
      else foreach (var p in parent.Parents) pending.Push(p);
    }
    return result;
  }

  private const int MaxLayers = 40;

  /// <summary>
  /// Iterations drawn with the current one: the EarlierShown iterations before it (blue) and - once
  /// the run has finished - the LaterShown after it (green); with a range, only those inside it, and
  /// its start and end always (also while it plays).
  /// </summary>
  private IEnumerable<(int Number, LayerKind Kind, double Age)> Layers() {
    var numbers = iterations.Select(i => i.Number).ToList();
    int at = numbers.IndexOf(CurrentIteration);
    if (at < 0) yield break;
    var before = numbers.Take(at).ToList();
    var after = Trace?.IsFinished == true ? numbers.Skip(at + 1).ToList() : [];
    if (Range is var (from, to)) {
      before = before.Where(n => n >= from).ToList();
      after = after.Where(n => n <= to).ToList();
      if (before.Count > 0 && before[0] == from) { yield return (from, LayerKind.RangeStart, 0); before.RemoveAt(0); }
      if (after.Count > 0 && after[^1] == to) { yield return (to, LayerKind.RangeEnd, 0); after.RemoveAt(after.Count - 1); }
    }
    before = Spread(before.TakeLast((int)EarlierShown).ToList(), MaxLayers);
    after = Spread(after.Take((int)LaterShown).ToList(), MaxLayers);
    for (int i = 0; i < before.Count; i++) yield return (before[i], LayerKind.Earlier, before.Count == 1 ? 0 : 1 - (double)i / (before.Count - 1));
    for (int i = 0; i < after.Count; i++) yield return (after[i], LayerKind.Later, after.Count == 1 ? 0 : (double)i / (after.Count - 1));
  }

  private static List<int> Spread(List<int> items, int count) =>
    items.Count <= count ? items : Enumerable.Range(0, count).Select(i => items[(int)Math.Round(i * (items.Count - 1.0) / (count - 1))]).Distinct().ToList();

  // ---- animation between the two selected iterations

  public IReadOnlyList<double> Speeds { get; } = [0.5, 1, 2, 5, 10, 25];

  /// <summary>Iterations per second.</summary>
  [ObservableProperty]
  public partial double Speed { get; set; } = 2;

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(PlayCommand), nameof(StopAnimationCommand))]
  public partial bool IsPlaying { get; set; }

  private Avalonia.Threading.DispatcherTimer? animation;

  partial void OnSpeedChanged(double value) {
    if (animation != null) animation.Interval = TimeSpan.FromSeconds(1 / Math.Max(0.1, value));
  }

  private bool CanPlay() => !IsPlaying && Range != null && iterations.Count > 0;

  /// <summary>Plays the selected range iteration by iteration (from its start, or on from the current one inside it).</summary>
  [RelayCommand(CanExecute = nameof(CanPlay))]
  private void Play() {
    if (Range is not var (from, to)) return;
    if (CurrentIteration < from || CurrentIteration >= to) GoTo(from, best: true);
    animation = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromSeconds(1 / Math.Max(0.1, Speed)), Avalonia.Threading.DispatcherPriority.Background, (_, _) => AnimationTick());
    animation.Start();
    IsPlaying = true;
  }

  /// <summary>One frame: the next iteration, until the end of the range.</summary>
  public void AnimationTick() {
    if (Range is not var (_, to) || CurrentIteration >= to) { StopAnimation(); return; }
    MoveIteration(+1);
    if (CurrentIteration >= to) StopAnimation();
  }

  private bool CanStopAnimation() => IsPlaying;

  [RelayCommand(CanExecute = nameof(CanStopAnimation))]
  private void StopAnimation() {
    animation?.Stop();
    animation = null;
    IsPlaying = false;
  }

  /// <summary>Population algorithms create many solutions per iteration; trajectory ones (tabu search, ...) one: then steps are iterations.</summary>
  public bool HasSteps => iterations.Any(i => i.StepCount > 1);

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
