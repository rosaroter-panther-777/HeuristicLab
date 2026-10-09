using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HeuristicLab.Studio.ViewModels;

namespace HeuristicLab.Studio.Views;

public partial class CatalogPickerWindow : Window {
  public CatalogPickerWindow() => InitializeComponent();

  private void Accept(object? sender, RoutedEventArgs e) {
    if (DataContext is CatalogPickerViewModel { Selected: not null }) Close(true);
  }

  private void ListDoubleTapped(object? sender, TappedEventArgs e) => Accept(sender, e);

  private void Cancel(object? sender, RoutedEventArgs e) => Close(false);
}
