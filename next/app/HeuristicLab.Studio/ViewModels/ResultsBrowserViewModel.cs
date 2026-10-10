using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Studio.Services;

namespace HeuristicLab.Studio.ViewModels;

public sealed record StoredRunRow(string Started, string Optimizer, string Seed, string Outcome, string Metric);
public sealed record SummaryRow(string Group, int Count, string Mean, string StdDev, string Min, string Median, string Max);

/// <summary>Results folder (written by runs with a results folder or "hl run --store"): runs and grouped statistics.</summary>
public partial class ResultsBrowserViewModel(IFileDialogService? fileDialogs = null) : ViewModelBase {
  private IReadOnlyList<IReadOnlyDictionary<string, string>> rows = [];

  [ObservableProperty]
  public partial string? Folder { get; set; }

  [ObservableProperty]
  public partial IReadOnlyList<string> Metrics { get; set; } = [];

  [ObservableProperty]
  public partial string? Metric { get; set; }

  [ObservableProperty]
  public partial IReadOnlyList<string> GroupColumns { get; set; } = [];

  [ObservableProperty]
  public partial string GroupBy { get; set; } = "optimizer";

  [ObservableProperty]
  public partial string Status { get; set; } = "Open a results folder to compare runs.";

  public ObservableCollection<StoredRunRow> Runs { get; } = [];
  public ObservableCollection<SummaryRow> Summary { get; } = [];

  [RelayCommand]
  private async Task OpenFolderAsync() {
    if (fileDialogs != null && await fileDialogs.PickFolderAsync("Open results folder") is string folder) Load(folder);
  }

  [RelayCommand]
  private void Reload() {
    if (Folder != null) Load(Folder);
  }

  public void Load(string folder) {
    Folder = folder;
    try {
      rows = new ResultStore(folder).Rows();
    } catch (Exception e) {
      rows = [];
      Status = $"Cannot read {folder}: {e.Message}";
      return;
    }
    Metrics = rows.SelectMany(r => r.Keys).Where(k => k.StartsWith("result:", StringComparison.Ordinal))
      .Distinct().Select(k => k["result:".Length..]).Order(StringComparer.OrdinalIgnoreCase).ToList();
    GroupColumns = new[] { "optimizer", "optimizerType", "inputFile", "runtime", "os" }
      .Concat(rows.SelectMany(r => r.Keys).Where(k => k.StartsWith("label:", StringComparison.Ordinal)).Distinct().Order(StringComparer.OrdinalIgnoreCase))
      .Concat(rows.SelectMany(r => r.Keys).Where(k => k.StartsWith("param:", StringComparison.Ordinal)).Distinct().Order(StringComparer.OrdinalIgnoreCase))
      .ToList();
    // sweeps are compared by configuration, walk-forward runs by fold
    GroupBy = GroupColumns.Contains("label:config") ? "label:config" : GroupColumns.Contains("label:fold") ? "label:fold" : "optimizer";
    Metric = new[] { "BestQuality", "CurrentBestQuality" }.FirstOrDefault(Metrics.Contains) ?? Metrics.FirstOrDefault();
    Refresh();
    Status = $"{rows.Count} runs in {folder}";
  }

  partial void OnMetricChanged(string? value) => Refresh();
  partial void OnGroupByChanged(string value) => Refresh();

  private void Refresh() {
    Runs.Clear();
    Summary.Clear();
    var metricKey = Metric == null ? null : "result:" + Metric;
    foreach (var r in rows)
      Runs.Add(new StoredRunRow(Value(r, "startedAt") is { Length: >= 19 } s ? s[..19].Replace('T', ' ') : "",
        Value(r, "optimizer"), Value(r, "seed"), Value(r, "outcome"), metricKey == null ? "" : Value(r, metricKey)));
    if (metricKey == null) return;
    foreach (var group in rows.Where(r => Value(r, "outcome") == "Completed").GroupBy(r => Value(r, GroupBy)).OrderBy(g => g.Key, StringComparer.Ordinal)) {
      var stats = Statistics.Of(group.Select(r => Value(r, metricKey)).Where(v => v != "")
        .Select(v => double.Parse(v, CultureInfo.InvariantCulture)));
      if (stats != null)
        Summary.Add(new SummaryRow(group.Key, stats.Count, G(stats.Mean), G(stats.StdDev), G(stats.Min), G(stats.Median), G(stats.Max)));
    }
  }

  private static string Value(IReadOnlyDictionary<string, string> row, string key) => row.TryGetValue(key, out var v) ? v : "";
  private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
