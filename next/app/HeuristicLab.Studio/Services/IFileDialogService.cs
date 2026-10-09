using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace HeuristicLab.Studio.Services;

/// <summary>File dialogs behind an interface, so view models can be tested without a window.</summary>
public interface IFileDialogService {
  Task<string?> OpenHlFileAsync();
  Task<string?> SaveHlFileAsync(string suggestedName);
}

public sealed class FileDialogService(TopLevel topLevel) : IFileDialogService {
  private static readonly FilePickerFileType HlFiles = new("HeuristicLab files") { Patterns = ["*.hl"] };

  public async Task<string?> OpenHlFileAsync() {
    var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Open HeuristicLab file", AllowMultiple = false, FileTypeFilter = [HlFiles]
    });
    return files.Count > 0 ? files[0].TryGetLocalPath() : null;
  }

  public async Task<string?> SaveHlFileAsync(string suggestedName) {
    var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Save HeuristicLab file", SuggestedFileName = suggestedName, DefaultExtension = "hl",
      FileTypeChoices = [HlFiles]
    });
    return file?.TryGetLocalPath();
  }
}
