using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HeuristicLab.Next.Runtime.Visuals;
using HeuristicLab.Studio.Controls;

namespace HeuristicLab.Studio.ViewModels;

/// <summary>A chart ready for <see cref="LineChart"/>.</summary>
public sealed record ChartPanel(string Title, IReadOnlyList<ChartSeries> Series, string XAxisTitle, string YAxisTitle,
                                string SecondYAxisTitle, IReadOnlyList<ChartMarker> Markers) {
  public static ChartPanel From(ChartVisual chart) => new(chart.Title,
    chart.Series.Select((s, i) => new ChartSeries(s.Name, ToColor(s.Color ?? Rgb.Palette(i)), s.Points.Select(p => new ChartPoint(p.X, p.Y)).ToList(),
      Kind: s.Kind, SecondYAxis: s.SecondYAxis, Width: s.Width)).ToList(),
    chart.XAxisTitle, chart.YAxisTitle, chart.SecondYAxisTitle, chart.Markers.Select(m => new ChartMarker(m.X, m.Label)).ToList());

  private static Color ToColor(Rgb c) => Color.FromArgb(c.A, c.R, c.G, c.B);
}

/// <summary>
/// The pictures of an item (from the runtime's Visualizations), one shown at a time. Refreshed live:
/// the chosen picture stays chosen (by title, else by position) and the controls keep their zoom
/// or rotation, since only the shown data is replaced.
/// </summary>
public partial class VisualsViewModel : ViewModelBase {
  private IReadOnlyList<Visual> visuals = [];

  [ObservableProperty]
  public partial IReadOnlyList<string> Titles { get; set; } = [];

  [ObservableProperty]
  public partial int SelectedIndex { get; set; } = -1;

  /// <summary>A <see cref="ChartPanel"/>, or the scene, boxes or text visual itself.</summary>
  [ObservableProperty]
  public partial object? Current { get; set; }

  [ObservableProperty]
  public partial string Title { get; set; } = "";

  [ObservableProperty]
  public partial string Notes { get; set; } = "";

  public bool HasAny => Titles.Count > 0;
  public bool HasChoice => Titles.Count > 1;
  public bool HasNotes => Notes.Length > 0;

  partial void OnTitlesChanged(IReadOnlyList<string> value) {
    OnPropertyChanged(nameof(HasAny));
    OnPropertyChanged(nameof(HasChoice));
  }

  partial void OnNotesChanged(string value) => OnPropertyChanged(nameof(HasNotes));

  partial void OnSelectedIndexChanged(int value) => ShowSelected();

  public IReadOnlyList<Visual> Visuals => visuals;

  public void Show(IReadOnlyList<Visual> next) {
    string? selectedTitle = SelectedIndex >= 0 && SelectedIndex < visuals.Count ? visuals[SelectedIndex].Title : null;
    int index = selectedTitle == null ? 0 : next.Select(v => v.Title).ToList().IndexOf(selectedTitle);
    if (index < 0) index = Math.Clamp(SelectedIndex, 0, Math.Max(0, next.Count - 1));
    visuals = next;
    var titles = next.Select(v => v.Title).ToList();
    if (!titles.SequenceEqual(Titles)) Titles = titles;
    if (next.Count == 0) index = -1;
    if (SelectedIndex != index) SelectedIndex = index;
    else ShowSelected();
  }

  private void ShowSelected() {
    var visual = SelectedIndex >= 0 && SelectedIndex < visuals.Count ? visuals[SelectedIndex] : null;
    Current = visual is ChartVisual chart ? ChartPanel.From(chart) : visual;
    Title = visual?.Title ?? "";
    Notes = visual == null ? "" : string.Join("    ·    ", visual.Notes);
  }
}
