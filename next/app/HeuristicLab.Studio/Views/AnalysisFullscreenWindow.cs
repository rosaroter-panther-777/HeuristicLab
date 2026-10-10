using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using HeuristicLab.Studio.ViewModels;

namespace HeuristicLab.Studio.Views;

/// <summary>
/// Detailed analysis in fullscreen: the picture fills the screen; navigation and chart float over it
/// in a panel that is moved by its title bar and resized at its lower right corner. Esc or F11 leaves.
/// </summary>
public sealed class AnalysisFullscreenWindow : Window {
  private const double MinPanelWidth = 420, MinPanelHeight = 220;
  private readonly Canvas canvas = new();
  private Point? dragStart, resizeStart;
  private Point panelAtStart;
  private Size sizeAtStart;
  private bool moved;

  /// <summary>The floating panel with navigation and chart.</summary>
  public Border Panel { get; }

  public AnalysisFullscreenWindow(AnalysisViewModel analysis) {
    DataContext = analysis;
    Title = "Detailed analysis";
    WindowState = WindowState.FullScreen;
    this[!BackgroundProperty] = this.GetResourceObservable("SystemControlBackgroundAltHighBrush").ToBinding();

    var leave = new Button { Content = "✕ Leave fullscreen", Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center };
    leave.Click += (_, _) => Close();
    var titleBar = new Border {
      Padding = new Thickness(10, 4), Cursor = new Cursor(StandardCursorType.SizeAll),
      Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x6F, 0xD8), 0.18),
      Child = new DockPanel {
        Children = {
          WithDock(leave, Dock.Right),
          new TextBlock { Text = "Detailed analysis  ·  drag here to move, the corner to resize  ·  Esc or F11 leaves fullscreen",
                          VerticalAlignment = VerticalAlignment.Center, FontSize = 12 }
        }
      }
    };
    titleBar.PointerPressed += (_, e) => { dragStart = e.GetPosition(canvas); panelAtStart = new Point(Canvas.GetLeft(Panel!), Canvas.GetTop(Panel!)); e.Pointer.Capture(titleBar); };
    titleBar.PointerMoved += (_, e) => {
      if (dragStart is not { } start) return;
      moved = true;
      var to = panelAtStart + (e.GetPosition(canvas) - start);
      Canvas.SetLeft(Panel!, Math.Clamp(to.X, 0, Math.Max(0, canvas.Bounds.Width - 80)));
      Canvas.SetTop(Panel!, Math.Clamp(to.Y, 0, Math.Max(0, canvas.Bounds.Height - 40)));
    };
    titleBar.PointerReleased += (_, e) => { dragStart = null; e.Pointer.Capture(null); };

    var grip = new Border {
      Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
      Cursor = new Cursor(StandardCursorType.BottomRightCorner), Background = new SolidColorBrush(Colors.Gray, 0.5), CornerRadius = new CornerRadius(0, 0, 6, 0)
    };
    grip.PointerPressed += (_, e) => { resizeStart = e.GetPosition(canvas); sizeAtStart = new Size(Panel!.Width, Panel.Height); e.Pointer.Capture(grip); e.Handled = true; };
    grip.PointerMoved += (_, e) => {
      if (resizeStart is not { } start) return;
      var delta = e.GetPosition(canvas) - start;
      Panel!.Width = Math.Max(MinPanelWidth, sizeAtStart.Width + delta.X);
      Panel.Height = Math.Max(MinPanelHeight, sizeAtStart.Height + delta.Y);
    };
    grip.PointerReleased += (_, e) => { resizeStart = null; e.Pointer.Capture(null); };

    var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
    body.Children.Add(titleBar);
    var controls = new AnalysisControlsView { Margin = new Thickness(10, 0, 10, 10) };
    Grid.SetRow(controls, 1);
    body.Children.Add(controls);
    Grid.SetRow(grip, 1);
    body.Children.Add(grip);

    Panel = new Border {
      Width = 980, Height = 420, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
      BorderBrush = new SolidColorBrush(Colors.Gray, 0.6), ClipToBounds = true,
      BoxShadow = BoxShadows.Parse("0 4 18 0 #60000000"), Child = body
    };
    Panel[!Border.BackgroundProperty] = this.GetResourceObservable("SystemControlBackgroundAltHighBrush").ToBinding();
    canvas.Children.Add(Panel);

    Content = new Panel {
      Children = {
        new ContentControl { Content = analysis.Visuals, Margin = new Thickness(12) },
        canvas
      }
    };
    // the panel starts at the bottom, centred, like the lower half of the normal view
    Opened += (_, _) => PlacePanel();
    // the window reaches its full size after opening: place again until the user moves the panel
    SizeChanged += (_, _) => { if (!moved) PlacePanel(); };
    AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
  }

  private static Control WithDock(Control control, Dock dock) {
    DockPanel.SetDock(control, dock);
    return control;
  }

  private void PlacePanel() {
    double width = Bounds.Width > 0 ? Bounds.Width : 1200, height = Bounds.Height > 0 ? Bounds.Height : 800;
    Panel.Width = Math.Min(Panel.Width, width - 40);
    Canvas.SetLeft(Panel, Math.Max(0, (width - Panel.Width) / 2));
    Canvas.SetTop(Panel, Math.Max(0, height - Panel.Height - 24));
  }

  private void OnKey(object? sender, KeyEventArgs e) {
    if (e.Key is Key.Escape or Key.F11) {
      e.Handled = true;
      Close();
    }
  }
}
