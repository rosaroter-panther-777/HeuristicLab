using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using HeuristicLab.Next.Runtime;

namespace HeuristicLab.Studio.ViewModels;

/// <summary>Searchable list of catalog entries (everything the loaded plugins offer).</summary>
public partial class CatalogPickerViewModel(string title, IReadOnlyList<CatalogEntry> entries) : ViewModelBase {
  public string Title { get; } = title;
  public IReadOnlyList<CatalogEntry> All { get; } = entries;

  [ObservableProperty]
  public partial string Filter { get; set; } = "";

  [ObservableProperty]
  public partial CatalogEntry? Selected { get; set; }

  public IReadOnlyList<CatalogEntry> Visible => Filter.Length == 0 ? All
    : All.Where(e => e.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                  || e.Description.Contains(Filter, StringComparison.OrdinalIgnoreCase)).ToList();

  partial void OnFilterChanged(string value) {
    OnPropertyChanged(nameof(Visible));
    if (Selected == null || !Visible.Contains(Selected)) Selected = Visible.FirstOrDefault();
  }
}
