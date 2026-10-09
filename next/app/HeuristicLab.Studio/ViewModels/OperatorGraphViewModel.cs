using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeuristicLab.Core;
using HeuristicLab.Next.Runtime;

namespace HeuristicLab.Studio.ViewModels;

public sealed record GraphNodeView(IOperator Operator, double X, double Y, string Name, string Kind, bool IsInitial, bool HasGraph);
public sealed record GraphEdgeView(double X1, double Y1, double X2, double Y2, string Label) {
  public Avalonia.Point Start => new(X1, Y1);
  public Avalonia.Point End => new(X2, Y2);
  public double LabelX => (X1 + X2) / 2 + 4;
  public double LabelY => (Y1 + Y2) / 2 - 8;
}

/// <summary>
/// An operator graph drawn in layers from the initial operator. Selecting an operator shows its
/// name, type, breakpoint and parameters; operators with their own graph (e.g. a main loop) can
/// be opened, and Back returns to the enclosing graph.
/// </summary>
public partial class OperatorGraphViewModel : ViewModelBase {
  public const double NodeWidth = 190, NodeHeight = 44, LayerGap = 70, RowGap = 24;
  private readonly EditContext context;
  private readonly Stack<(OperatorGraph Graph, string Title)> path = new();

  public OperatorGraphViewModel(OperatorGraph graph, string title, EditContext context) {
    this.context = context;
    Show(graph, title);
  }

  [ObservableProperty]
  public partial string Title { get; set; } = "";

  [ObservableProperty]
  public partial IReadOnlyList<GraphNodeView> Nodes { get; set; } = [];

  [ObservableProperty]
  public partial IReadOnlyList<GraphEdgeView> Edges { get; set; } = [];

  [ObservableProperty]
  public partial double Width { get; set; }

  [ObservableProperty]
  public partial double Height { get; set; }

  [ObservableProperty]
  public partial GraphNodeView? Selected { get; set; }

  [ObservableProperty]
  public partial ValueEditorViewModel? SelectedEditor { get; set; }

  [ObservableProperty]
  public partial double Zoom { get; set; } = 1;

  public bool CanGoBack => path.Count > 1;

  partial void OnSelectedChanged(GraphNodeView? value) {
    SelectedEditor = value == null ? null : new ValueEditorViewModel(value.Operator, context);
    OpenCommand.NotifyCanExecuteChanged();
  }

  public void Select(IOperator op) => Selected = Nodes.FirstOrDefault(n => n.Operator == op);

  [RelayCommand(CanExecute = nameof(CanOpen))]
  private void Open() {
    if (Selected != null && ItemInspector.GraphOf(Selected.Operator) is OperatorGraph g) Show(g, Selected.Name);
  }
  private bool CanOpen() => Selected?.HasGraph == true;

  [RelayCommand]
  private void Back() {
    if (path.Count < 2) return;
    path.Pop();
    var (graph, title) = path.Pop();
    Show(graph, title);
  }

  private void Show(OperatorGraph graph, string title) {
    path.Push((graph, title));
    Title = string.Join(" › ", path.Reverse().Select(p => p.Title));
    var layout = ItemInspector.Layout(graph);
    var position = layout.Nodes.ToDictionary(n => n.Operator,
      n => (X: 20 + n.Row * (NodeWidth + RowGap), Y: 20 + n.Layer * (NodeHeight + LayerGap)));
    Nodes = layout.Nodes.Select(n => new GraphNodeView(n.Operator, position[n.Operator].X, position[n.Operator].Y,
      n.Operator.Name, n.Operator.ItemName, n.Operator == layout.Initial, ItemInspector.GraphOf(n.Operator) != null)).ToList();
    Edges = layout.Edges.Select(e => {
      var (x1, y1) = position[e.From];
      var (x2, y2) = position[e.To];
      return new GraphEdgeView(x1 + NodeWidth / 2, y1 + NodeHeight, x2 + NodeWidth / 2, y2, e.Label);
    }).ToList();
    Width = Nodes.Count == 0 ? 400 : Nodes.Max(n => n.X) + NodeWidth + 20;
    Height = Nodes.Count == 0 ? 200 : Nodes.Max(n => n.Y) + NodeHeight + 20;
    Selected = Nodes.FirstOrDefault(n => n.IsInitial) ?? Nodes.FirstOrDefault();
    OnPropertyChanged(nameof(CanGoBack));
  }
}
