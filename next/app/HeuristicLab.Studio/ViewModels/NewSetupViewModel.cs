using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Optimization;
using HeuristicLab.Studio.Services;

namespace HeuristicLab.Studio.ViewModels;

/// <summary>Choice in a picker; Entry is null for "(none)" / "(algorithm default)".</summary>
public sealed record Choice(string Display, CatalogEntry? Entry);

/// <summary>
/// "New" dialog: algorithm, compatible problem, then a benchmark instance or a data file.
/// Builds the algorithm with Setups.Create, the same code path as "hl new".
/// </summary>
public partial class NewSetupViewModel : ViewModelBase {
  private const string NoInstance = "(none)";
  private readonly IFileDialogService? fileDialogs;

  /// <param name="fixedAlgorithm">Only choose the problem for this algorithm (the experiment builder's "Add problem").</param>
  public NewSetupViewModel(IFileDialogService? fileDialogs = null, CatalogEntry? fixedAlgorithm = null) {
    this.fileDialogs = fileDialogs;
    ChoosesAlgorithm = fixedAlgorithm == null;
    Algorithms = fixedAlgorithm != null ? [new Choice(fixedAlgorithm.Name, fixedAlgorithm)]
      : Catalog.Algorithms().Select(a => new Choice(a.Name, a)).ToList();
    SelectedAlgorithm = Algorithms.FirstOrDefault(a => a.Entry?.Type.Name == "GeneticAlgorithm") ?? Algorithms.FirstOrDefault();
  }

  public bool ChoosesAlgorithm { get; }
  public string WindowTitle => ChoosesAlgorithm ? "New" : $"Problem for {SelectedAlgorithm?.Display}";
  public string CreateText => ChoosesAlgorithm ? "Create" : "Use problem";

  public IReadOnlyList<Choice> Algorithms { get; }

  [ObservableProperty]
  public partial Choice? SelectedAlgorithm { get; set; }

  [ObservableProperty]
  public partial IReadOnlyList<Choice> Problems { get; set; } = [];

  [ObservableProperty]
  public partial Choice? SelectedProblem { get; set; }

  [ObservableProperty]
  public partial IReadOnlyList<string> Instances { get; set; } = [];

  [ObservableProperty]
  public partial string? SelectedInstance { get; set; }

  [ObservableProperty]
  public partial bool AcceptsData { get; set; }

  [ObservableProperty]
  public partial string? DataFile { get; set; }

  [ObservableProperty]
  public partial IReadOnlyList<string> Columns { get; set; } = [];

  [ObservableProperty]
  public partial string? Target { get; set; }

  [ObservableProperty]
  public partial string TrainingPercentText { get; set; } = "66";

  [ObservableProperty]
  public partial string TrainingStartText { get; set; } = "0";

  [ObservableProperty]
  public partial string? Error { get; set; }

  /// <summary>Set by Create when the setup succeeded.</summary>
  public SetupResult? Result { get; private set; }

  partial void OnSelectedAlgorithmChanged(Choice? value) {
    var problems = new List<Choice>();
    // "(default)" only if the algorithm creates its own problem (e.g. Linear Regression does not)
    var own = (value?.Entry?.CreateInstance() as IAlgorithm)?.Problem;
    if (own != null) problems.Add(new Choice($"(default: {own.ItemName})", null));
    if (value?.Entry != null) problems.AddRange(Setups.ProblemsFor(value.Entry).Select(p => new Choice(p.Name, p)));
    Problems = problems;
    SelectedProblem = problems.FirstOrDefault();
  }

  partial void OnSelectedProblemChanged(Choice? value) {
    var problem = value?.Entry?.CreateInstance() as IProblem
                  ?? (SelectedAlgorithm?.Entry?.CreateInstance() as IAlgorithm)?.Problem;
    Instances = problem == null ? [] : [NoInstance, .. ProblemInstances.For(problem).Select(i => i.QualifiedName)];
    SelectedInstance = Instances.FirstOrDefault();
    AcceptsData = problem != null && DataProblems.CanLoad(problem);
    if (!AcceptsData) DataFile = null;
  }

  partial void OnDataFileChanged(string? value) {
    Columns = [];
    Target = null;
    if (value == null) return;
    try {
      var table = DataFiles.Read(value);
      Columns = table.NumericNames.ToList();
      Target = Columns.LastOrDefault();
      Error = null;
    } catch (Exception e) {
      Error = $"Cannot read {value}: {e.Message}";
    }
  }

  [RelayCommand]
  private async Task BrowseDataAsync() {
    if (fileDialogs != null && await fileDialogs.OpenDataFileAsync() is string path) DataFile = path;
  }

  /// <summary>Builds the algorithm; returns false and sets Error if the input is incomplete or invalid.</summary>
  public bool Create() {
    Error = null;
    if (SelectedAlgorithm?.Entry == null) { Error = "Choose an algorithm."; return false; }
    if (!int.TryParse(TrainingPercentText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var training)
        || !int.TryParse(TrainingStartText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)) {
      Error = "Training % and warm-up rows must be whole numbers.";
      return false;
    }
    bool useInstance = SelectedInstance != null && SelectedInstance != NoInstance;
    bool useData = AcceptsData && DataFile != null;
    if (useInstance && useData) { Error = "Choose either a benchmark instance or a data file."; return false; }
    var request = new SetupRequest(SelectedAlgorithm.Entry.TypeName) {
      Problem = SelectedProblem?.Entry?.TypeName,
      Instance = useInstance ? SelectedInstance : null,
      DataFile = useData ? DataFile : null,
      Target = useData ? Target : null,
      TrainingPercent = training, TrainingStart = start
    };
    // the algorithm's default problem is used for instances/data when no problem was chosen
    if (request.Problem == null && (useInstance || useData) &&
        (SelectedAlgorithm.Entry.CreateInstance() as IAlgorithm)?.Problem is IProblem own)
      request = request with { Problem = own.GetType().FullName };
    try {
      Result = Setups.Create(request);
      return true;
    } catch (ArgumentException e) {
      Error = e.Message;
      return false;
    }
  }
}
