using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HeuristicLab.Studio.ViewModels;

namespace HeuristicLab.Studio.Views;

public partial class AnalysisView : UserControl {
  public AnalysisView() {
    InitializeComponent();
    // F11 enters fullscreen (and leaves it again, there)
    AddHandler(KeyDownEvent, (_, e) => {
      if (e.Key == Key.F11) { e.Handled = true; OpenFullscreen(); }
    }, RoutingStrategies.Tunnel);
  }

  /// <summary>The open fullscreen window, if any (one at a time).</summary>
  public AnalysisFullscreenWindow? Fullscreen { get; private set; }

  private void OnFullscreen(object? sender, RoutedEventArgs e) => OpenFullscreen();

  public AnalysisFullscreenWindow? OpenFullscreen() {
    if (DataContext is not AnalysisViewModel { HasTrace: true } analysis || Fullscreen != null) return Fullscreen;
    Fullscreen = new AnalysisFullscreenWindow(analysis);
    Fullscreen.Closed += (_, _) => Fullscreen = null;
    if (TopLevel.GetTopLevel(this) is Window owner) Fullscreen.Show(owner);
    else Fullscreen.Show();
    return Fullscreen;
  }
}
