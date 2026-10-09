using Avalonia.Controls;
using Avalonia.Interactivity;
using HeuristicLab.Studio.ViewModels;

namespace HeuristicLab.Studio.Views;

public partial class NewSetupWindow : Window {
  public NewSetupWindow() => InitializeComponent();

  private void Create(object? sender, RoutedEventArgs e) {
    if (DataContext is NewSetupViewModel vm && vm.Create()) Close(true);
  }

  private void Cancel(object? sender, RoutedEventArgs e) => Close(false);
}
