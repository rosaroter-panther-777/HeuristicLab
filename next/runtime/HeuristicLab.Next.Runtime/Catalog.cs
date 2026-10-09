using HeuristicLab.Core;
using HeuristicLab.Optimization;
using HeuristicLab.PluginInfrastructure;

namespace HeuristicLab.Next.Runtime;

public sealed record CatalogEntry(string Name, string Description, string TypeName, Type Type) {
  public IItem CreateInstance() => (IItem)Activator.CreateInstance(Type)!;
}

/// <summary>Algorithms and problems that can be created, as discovered from the loaded plugins.</summary>
public static class Catalog {
  public static IReadOnlyList<CatalogEntry> Algorithms() => Entries(typeof(IAlgorithm));
  public static IReadOnlyList<CatalogEntry> Problems() => Entries(typeof(IProblem));
  /// <summary>Optimizers that are not algorithms: experiments, batch runs, time-limit runs.</summary>
  public static IReadOnlyList<CatalogEntry> MetaOptimizers() =>
    Entries(typeof(IOptimizer)).Where(e => !typeof(IAlgorithm).IsAssignableFrom(e.Type)).ToList();

  public static CatalogEntry? Find(IEnumerable<CatalogEntry> entries, string nameOrType) =>
    entries.FirstOrDefault(e => string.Equals(e.Name, nameOrType, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(e.TypeName, nameOrType, StringComparison.Ordinal)
                             || string.Equals(e.Type.Name, nameOrType, StringComparison.OrdinalIgnoreCase));

  private static IReadOnlyList<CatalogEntry> Entries(Type baseType) {
    HlRuntime.Initialize();
    return ApplicationManager.Manager.GetTypes(baseType)
      .Where(t => t.IsPublic && t.GetConstructor(Type.EmptyTypes) != null && !t.ContainsGenericParameters)
      .Select(t => new CatalogEntry(ItemAttribute.GetName(t), ItemAttribute.GetDescription(t), t.FullName!, t))
      .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();
  }
}
