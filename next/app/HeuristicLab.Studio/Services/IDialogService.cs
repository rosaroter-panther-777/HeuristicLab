using System.Threading.Tasks;
using Avalonia.Controls;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Studio.ViewModels;
using HeuristicLab.Studio.Views;

namespace HeuristicLab.Studio.Services;

/// <summary>Modal dialogs behind an interface, so view models can be tested without windows.</summary>
public interface IDialogService {
  /// <summary>Shows the "New" dialog; returns the created setup or null if cancelled.</summary>
  Task<SetupResult?> NewSetupAsync();
}

public sealed class DialogService(Window owner, IFileDialogService fileDialogs) : IDialogService {
  public async Task<SetupResult?> NewSetupAsync() {
    var viewModel = new NewSetupViewModel(fileDialogs);
    var dialog = new NewSetupWindow { DataContext = viewModel };
    return await dialog.ShowDialog<bool>(owner) ? viewModel.Result : null;
  }
}
