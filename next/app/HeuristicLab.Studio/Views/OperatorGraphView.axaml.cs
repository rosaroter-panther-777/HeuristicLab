using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using HeuristicLab.Studio.ViewModels;

namespace HeuristicLab.Studio.Views;

public partial class OperatorGraphView : UserControl {
  public OperatorGraphView() {
    InitializeComponent();
    // mark the selected node (styles cannot compare records with the view model's selection)
    DataContextChanged += (_, _) => {
      if (DataContext is OperatorGraphViewModel vm)
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(OperatorGraphViewModel.Selected)) MarkSelected(vm); };
    };
    LayoutUpdated += (_, _) => { if (DataContext is OperatorGraphViewModel vm) MarkSelected(vm); };
  }

  private void MarkSelected(OperatorGraphViewModel vm) {
    foreach (var border in this.GetVisualDescendants().OfType<Border>())
      if (border.DataContext is GraphNodeView node && border.Classes.Contains("node"))
        border.Classes.Set("selected", ReferenceEquals(node, vm.Selected));
  }

  private void NodeTapped(object? sender, TappedEventArgs e) {
    if ((sender as Control)?.DataContext is GraphNodeView node && DataContext is OperatorGraphViewModel vm) vm.Selected = node;
  }

  private void NodeDoubleTapped(object? sender, TappedEventArgs e) {
    if (DataContext is OperatorGraphViewModel vm && vm.OpenCommand.CanExecute(null)) vm.OpenCommand.Execute(null);
  }
}
