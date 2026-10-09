using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HeuristicLab.Studio.ViewModels;

namespace HeuristicLab.Studio.Views;

public partial class ExperimentWorkspaceView : UserControl {
  public ExperimentWorkspaceView() => InitializeComponent();

  private void BlockTapped(object? sender, TappedEventArgs e) {
    if ((sender as Control)?.DataContext is BlockViewModel block) block.SelectCommand.Execute(null);
  }

  private void BlockDoubleTapped(object? sender, TappedEventArgs e) {
    if ((sender as Control)?.DataContext is BlockViewModel block) block.RenameCommand.Execute(null);
  }

  private void NameKeyDown(object? sender, KeyEventArgs e) {
    if (e.Key is Key.Enter or Key.Escape && (sender as Control)?.DataContext is BlockViewModel block) {
      block.IsEditingName = false;
      e.Handled = true;
    }
  }

  private void NameLostFocus(object? sender, RoutedEventArgs e) {
    if ((sender as Control)?.DataContext is BlockViewModel block) block.IsEditingName = false;
  }

  // focus the inline name box when renaming starts
  private void NameBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e) {
    if (e.Property == IsVisibleProperty && e.NewValue is true && sender is TextBox box) {
      box.Focus();
      box.SelectAll();
    }
  }
}
