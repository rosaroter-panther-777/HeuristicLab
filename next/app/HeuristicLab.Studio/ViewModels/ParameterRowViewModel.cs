using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using HeuristicLab.Core;
using HeuristicLab.Next.Runtime;

namespace HeuristicLab.Studio.ViewModels;

/// <summary>
/// One editable parameter. Setting Value applies it through ParameterEditor; an invalid value is
/// reverted and reported via the error callback.
/// </summary>
public partial class ParameterRowViewModel(IParameterizedItem item, ParameterInfo info, Action<string> reportError) : ViewModelBase {
  public string Name { get; } = info.Name;
  public string Description { get; } = info.Description;
  public bool Editable { get; } = info.Editable;
  public IReadOnlyList<string> Choices { get; } = info.Choices;
  public bool IsChoice => Choices.Count > 0;
  public bool IsText => !IsChoice;

  private string value = info.Value;
  public string Value {
    get => value;
    set {
      if (value == this.value || !Editable) return;
      try {
        ParameterEditor.Set(item, Name, value);
        SetProperty(ref this.value, value);
      } catch (ArgumentException e) {
        reportError(e.Message);
        OnPropertyChanged(nameof(Value));  // UI shows the old value again
      }
    }
  }
}
