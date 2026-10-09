using System.Linq;
using System.Threading.Tasks;
using HeuristicLab.Optimization;
using Avalonia.Controls;
using HeuristicLab.Next.Runtime;
using HeuristicLab.Studio.ViewModels;
using HeuristicLab.Studio.Views;

namespace HeuristicLab.Studio.Services;

/// <summary>Modal dialogs behind an interface, so view models can be tested without windows.</summary>
public interface IDialogService {
  /// <summary>Shows the "New" dialog; returns the created setup or null if cancelled.</summary>
  Task<SetupResult?> NewSetupAsync();
  /// <summary>Chooses an algorithm type from the catalog; null if cancelled.</summary>
  Task<CatalogEntry?> PickAlgorithmAsync();
  /// <summary>Chooses and loads a problem (benchmark instance or data) for this algorithm; null if cancelled.</summary>
  Task<IProblem?> PickProblemAsync(IAlgorithm algorithm);
  Task<bool> ConfirmAsync(string title, string message);
}

public sealed class DialogService(Window owner, IFileDialogService fileDialogs) : IDialogService {
  public async Task<SetupResult?> NewSetupAsync() {
    var viewModel = new NewSetupViewModel(fileDialogs);
    var dialog = new NewSetupWindow { DataContext = viewModel };
    return await dialog.ShowDialog<bool>(owner) ? viewModel.Result : null;
  }

  public async Task<CatalogEntry?> PickAlgorithmAsync() {
    var viewModel = new CatalogPickerViewModel("Add algorithm", Catalog.Algorithms());
    var dialog = new CatalogPickerWindow { DataContext = viewModel };
    return await dialog.ShowDialog<bool>(owner) ? viewModel.Selected : null;
  }

  public async Task<IProblem?> PickProblemAsync(IAlgorithm algorithm) {
    var entry = Catalog.Algorithms().FirstOrDefault(a => a.Type == algorithm.GetType());
    if (entry == null) return null;
    var viewModel = new NewSetupViewModel(fileDialogs, entry);
    var dialog = new NewSetupWindow { DataContext = viewModel };
    if (!await dialog.ShowDialog<bool>(owner) || viewModel.Result?.Algorithm is not IAlgorithm scratch) return null;
    // the problem was built on a scratch algorithm of the same type; a clone carries no event
    // registrations of it (legacy algorithms cannot be detached: Problem = null throws)
    return scratch.Problem?.Clone() as IProblem;
  }

  public async Task<bool> ConfirmAsync(string title, string message) =>
    await new ConfirmWindow(title, message).ShowDialog<bool>(owner);
}
