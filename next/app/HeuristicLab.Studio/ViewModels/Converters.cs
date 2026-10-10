using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HeuristicLab.Studio.ViewModels;

public static class Converters {
  /// <summary>Bold text for true (e.g. significant test results).</summary>
  public static readonly IValueConverter BoldIfTrue = new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.Bold : FontWeight.Normal);
}
