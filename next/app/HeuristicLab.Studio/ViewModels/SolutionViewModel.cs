using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Optimization;
using HeuristicLab.Studio.Controls;

namespace HeuristicLab.Studio.ViewModels;

/// <summary>"Solution" tab: what the optimizer found, as model text, metrics and a chart.</summary>
public partial class SolutionViewModel : ViewModelBase {
  [ObservableProperty]
  public partial IReadOnlyList<SolutionView> Solutions { get; set; } = [];

  [ObservableProperty]
  public partial SolutionView? Selected { get; set; }

  [ObservableProperty]
  public partial string Heading { get; set; } = "Run an algorithm (or open a .hl file that has run) to see its solutions.";

  [ObservableProperty]
  public partial string? Model { get; set; }

  [ObservableProperty]
  public partial IReadOnlyList<ChartSeries> Series { get; set; } = [];

  [ObservableProperty]
  public partial IReadOnlyList<ChartMarker> Markers { get; set; } = [];

  [ObservableProperty]
  public partial string XAxisTitle { get; set; } = "Row";

  [ObservableProperty]
  public partial string YAxisTitle { get; set; } = "";

  public ObservableCollection<NameValue> Metrics { get; } = [];

  public void Show(IOptimizer? optimizer) {
    Solutions = optimizer == null ? [] : HeuristicLab.Next.Runtime.Solutions.Find(optimizer);
    Selected = Solutions.FirstOrDefault();
    if (Selected == null) Clear();
  }

  partial void OnSelectedChanged(SolutionView? value) {
    if (value == null) { Clear(); return; }
    Heading = $"{value.Name}: {value.Title} ({value.Kind})";
    Model = value.Model;
    Metrics.Clear();
    foreach (var (name, number) in value.Metrics.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
      Metrics.Add(new NameValue(name, number.ToString("G8", CultureInfo.InvariantCulture)));
    BuildChart(value);
  }

  private void BuildChart(SolutionView v) {
    var markers = new List<ChartMarker>();
    if (v.Test.End > v.Test.Start && v.Test.Start > 0) markers.Add(new ChartMarker(v.Test.Start, "test"));
    switch (v.Kind) {
      case SolutionKind.Route:
        Series = [new ChartSeries("Tour", ChartSeries.PaletteColor(0), v.Route!.Select(p => new ChartPoint(p.X, p.Y)).ToList())];
        XAxisTitle = "x"; YAxisTitle = "y"; markers.Clear();
        break;
      case SolutionKind.Trading:
        Series = [
          new ChartSeries("Equity (training)", ChartSeries.PaletteColor(0), v.TrainingEquity!.Select((e, i) => new ChartPoint(v.Training.Start + i, e)).ToList()),
          new ChartSeries("Equity (test)", ChartSeries.PaletteColor(1), v.TestEquity!.Select((e, i) => new ChartPoint(v.Test.Start + i, e)).ToList())
        ];
        XAxisTitle = "Row"; YAxisTitle = "Cumulative profit";
        break;
      default:
        Series = [
          new ChartSeries(v.TargetName ?? "Actual", ChartSeries.PaletteColor(0), v.Actual!.Select((y, i) => new ChartPoint(i, y)).ToList()),
          new ChartSeries("Predicted", ChartSeries.PaletteColor(1), v.Predicted!.Select((y, i) => new ChartPoint(i, y)).ToList())
        ];
        XAxisTitle = "Row"; YAxisTitle = v.TargetName ?? "";
        break;
    }
    Markers = markers;
  }

  private void Clear() {
    Heading = "No solutions yet. Run the algorithm to see what it finds.";
    Model = null;
    Metrics.Clear();
    Series = [];
    Markers = [];
  }
}
