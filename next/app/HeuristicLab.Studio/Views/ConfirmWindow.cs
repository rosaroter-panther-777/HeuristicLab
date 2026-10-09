using Avalonia.Controls;
using Avalonia.Layout;

namespace HeuristicLab.Studio.Views;

/// <summary>Yes/no question; ShowDialog returns true for yes.</summary>
public sealed class ConfirmWindow : Window {
  public ConfirmWindow(string title, string message) {
    Title = title;
    Width = 420;
    SizeToContent = SizeToContent.Height;
    CanResize = false;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    var yes = new Button { Content = "Delete", Classes = { "accent" } };
    var no = new Button { Content = "Cancel" };
    yes.Click += (_, _) => Close(true);
    no.Click += (_, _) => Close(false);
    Content = new StackPanel {
      Margin = new(16), Spacing = 14,
      Children = {
        new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
        new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { no, yes } }
      }
    };
  }
}
