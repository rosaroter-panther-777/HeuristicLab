using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeuristicLab.Core;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Scripting;

namespace HeuristicLab.Studio.ViewModels;

/// <summary>"Scripts" tab: open C# scripts (HeuristicLab's CSharpScript), one shown at a time.</summary>
public partial class ScriptsViewModel : ViewModelBase {
  public ObservableCollection<ScriptViewModel> Scripts { get; } = [];

  [ObservableProperty]
  public partial ScriptViewModel? Selected { get; set; }

  public bool HasScripts => Scripts.Count > 0;

  public ScriptViewModel Open(CSharpScript script) {
    var vm = new ScriptViewModel(script);
    Scripts.Add(vm);
    Selected = vm;
    OnPropertyChanged(nameof(HasScripts));
    return vm;
  }

  [RelayCommand]
  private void NewScript() => Open(new CSharpScript { Name = $"Script {Scripts.Count + 1}" });

  [RelayCommand]
  private void Close(ScriptViewModel? script) {
    if (script == null || script.IsRunning) return;
    Scripts.Remove(script);
    if (Selected == script) Selected = Scripts.LastOrDefault();
    OnPropertyChanged(nameof(HasScripts));
  }
}

public sealed record CompileErrorEntry(string Location, string Text);

public sealed record VariableEntry(string Name, string Type, string Value, object? Raw);

/// <summary>
/// One script: its code, compiled and run as HeuristicLab runs scripts (Main() of a class deriving
/// from CSharpScriptBase, with vars for shared variables). Console output, compile errors and the
/// variables it leaves behind are shown; variables holding HeuristicLab items get the same
/// visualizations as results. A running script cannot be killed: .NET has no thread abort.
/// </summary>
public partial class ScriptViewModel : ViewModelBase {
  private readonly StringBuilder output = new();
  private readonly DispatcherTimer clock;
  private DateTime started;

  public ScriptViewModel(CSharpScript script) {
    Script = script;
    Code = script.Code;
    script.ConsoleOutputChanged += (_, e) => Dispatcher.UIThread.Post(() => { output.Append(e.Value); Output = output.ToString(); });
    clock = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Elapsed = Format(DateTime.UtcNow - started));
  }

  public CSharpScript Script { get; }

  public string Name { get => Script.Name; set { if (Script.Name != value && !string.IsNullOrWhiteSpace(value)) { Script.Name = value; OnPropertyChanged(); } } }

  public string Description => Script.Description;

  [ObservableProperty]
  public partial string Code { get; set; }

  partial void OnCodeChanged(string value) {
    if (Script.Code != value) Script.Code = value;
  }

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(RunCommand))]
  public partial bool IsRunning { get; set; }

  [ObservableProperty]
  public partial string Output { get; set; } = "";

  [ObservableProperty]
  public partial string Status { get; set; } = "Not run yet.";

  [ObservableProperty]
  public partial string Elapsed { get; set; } = "";

  [ObservableProperty]
  public partial IReadOnlyList<CompileErrorEntry> Errors { get; set; } = [];

  [ObservableProperty]
  public partial IReadOnlyList<VariableEntry> Variables { get; set; } = [];

  [ObservableProperty]
  public partial VariableEntry? SelectedVariable { get; set; }

  [ObservableProperty]
  public partial ResultDetailViewModel? VariableDetail { get; set; }

  /// <summary>Output, Errors or Variables.</summary>
  [ObservableProperty]
  public partial int SelectedTab { get; set; }

  public const int OutputTab = 0, ErrorsTab = 1, VariablesTab = 2;

  partial void OnSelectedVariableChanged(VariableEntry? value) =>
    VariableDetail = value?.Raw is IItem item ? new ResultDetailViewModel(value.Name, item, new EditContext(_ => { }, () => false)) : null;

  private static string Format(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss\.f");

  private bool CanRun() => !IsRunning;

  /// <summary>Compiles and runs the script on a background thread; returns when it has finished.</summary>
  [RelayCommand(CanExecute = nameof(CanRun))]
  public async Task RunAsync() {
    IsRunning = true;
    output.Clear();
    Output = "";
    Errors = [];
    Status = "Compiling ...";
    try {
      try {
        await Task.Run(() => Script.Compile());
      } catch (Exception) {
        Errors = Script.CompileErrors.Cast<CompilerError>().Where(e => !e.IsWarning)
          .Select(e => new CompileErrorEntry($"line {e.Line}, column {e.Column}", e.ErrorText)).ToList();
        Status = $"Compilation failed: {Errors.Count} error{(Errors.Count == 1 ? "" : "s")}.";
        SelectedTab = ErrorsTab;
        return;
      }
      Status = "Running ...";
      SelectedTab = OutputTab;
      started = DateTime.UtcNow;
      clock.Start();
      Exception? error = null;
      void Finished(object? sender, HeuristicLab.Common.EventArgs<Exception> e) => error = e.Value;
      Script.ScriptExecutionFinished += Finished;
      // a thread of its own: scripts run long loops, and some (LibSVM) write to the console directly
      var done = new TaskCompletionSource();
      var thread = new Thread(() => { try { Script.Execute(); } finally { done.SetResult(); } }) { IsBackground = true, Name = "Script " + Script.Name };
      thread.Start();
      await done.Task;
      Script.ScriptExecutionFinished -= Finished;
      clock.Stop();
      Elapsed = Format(DateTime.UtcNow - started);
      Dispatcher.UIThread.RunJobs();  // output posted by the script thread
      Status = error == null ? $"Finished after {Elapsed}." : $"Failed after {Elapsed}: {error.GetBaseException().Message}";
      if (error != null) { output.AppendLine().AppendLine(error.ToString()); Output = output.ToString(); }
      RefreshVariables();
    } finally {
      IsRunning = false;
    }
  }

  public void RefreshVariables() {
    var selected = SelectedVariable?.Name;
    Variables = Script.VariableStore.ToArray().Select(v => new VariableEntry(v.Key, v.Value?.GetType().Name ?? "null",
      v.Value is IItem item ? ItemInspector.Summary(item) : Convert.ToString(v.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "", v.Value)).ToList();
    SelectedVariable = Variables.FirstOrDefault(v => v.Name == selected);
  }
}
