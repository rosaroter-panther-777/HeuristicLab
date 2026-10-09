using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HeuristicLab.Studio.Services;
using HeuristicLab.Studio.ViewModels;
using HeuristicLab.Studio.Views;

namespace HeuristicLab.Studio;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var viewModel = new MainViewModel(new FileDialogService(window));
            window.DataContext = viewModel;
            desktop.MainWindow = window;

            // "HeuristicLab.Studio file.hl" opens the file at startup
            var file = desktop.Args?.FirstOrDefault(a => a.EndsWith(".hl", System.StringComparison.OrdinalIgnoreCase));
            if (file != null && File.Exists(file)) _ = viewModel.LoadAsync(file);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
