using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Studio.ViewModels;

namespace HeuristicLab.Studio.Views;

public partial class SamplesView : UserControl {
  public SamplesView() => InitializeComponent();

  // one list per group, one selection overall
  private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) {
    if (sender is not ListBox list || list.SelectedItem is not SampleInfo sample || DataContext is not SamplesViewModel vm) return;
    vm.Selected = sample;
    foreach (var other in this.GetVisualDescendants().OfType<ListBox>().Where(l => l != list)) other.SelectedItem = null;
  }

  private void OnDoubleTapped(object? sender, TappedEventArgs e) {
    if (DataContext is SamplesViewModel vm && vm.OpenCommand.CanExecute(null)) vm.OpenCommand.Execute(null);
  }
}
