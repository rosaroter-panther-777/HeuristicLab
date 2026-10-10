using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeuristicLab.Next.Runtime;

namespace HeuristicLab.Studio.ViewModels;

public sealed record SampleGroup(string Name, IReadOnlyList<SampleInfo> Samples);

/// <summary>
/// The start page: HeuristicLab's samples in their groups (standard problems, data analysis,
/// scripts). Opening one adds its algorithm as an experiment, or opens a script in the Scripts tab.
/// </summary>
public partial class SamplesViewModel(Func<SampleInfo, Task> open) : ViewModelBase {
  [ObservableProperty]
  public partial IReadOnlyList<SampleGroup> Groups { get; set; } = [];

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
  public partial SampleInfo? Selected { get; set; }

  [ObservableProperty]
  public partial bool IsLoading { get; set; }

  [ObservableProperty]
  public partial string Message { get; set; } = "";

  private Task? loading;

  /// <summary>Reads names and descriptions of the samples (once; in the background).</summary>
  public Task LoadAsync() => loading ??= Load();

  private async Task Load() {
    IsLoading = true;
    try {
      var samples = await Samples.ListAsync();
      Groups = samples.GroupBy(s => s.Group).Select(g => new SampleGroup(g.Key, g.ToList())).ToList();
    } catch (Exception e) {
      Message = "Could not load the samples: " + e.Message;
    } finally {
      IsLoading = false;
    }
  }

  [RelayCommand(CanExecute = nameof(CanOpen))]
  private Task OpenAsync() => Selected is { } sample ? open(sample) : Task.CompletedTask;

  private bool CanOpen() => Selected != null;

  /// <summary>Opens a sample by its file name, e.g. "GA_TSP".</summary>
  public async Task OpenAsync(string id) {
    await LoadAsync();
    var sample = Groups.SelectMany(g => g.Samples).Single(s => s.Id == id);
    Selected = sample;
    await open(sample);
  }
}
