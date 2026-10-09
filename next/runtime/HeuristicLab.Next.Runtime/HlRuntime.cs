using System.Reflection;
using HeuristicLab.Common;
using HeuristicLab.Core;

namespace HeuristicLab.Next.Runtime;

/// <summary>
/// One-time initialization of the HeuristicLab core for a process. Plugin discovery and .hl
/// deserialization only see assemblies that are loaded, so all HeuristicLab assemblies next to
/// the application are loaded up front (as the legacy plugin infrastructure did).
/// </summary>
public static class HlRuntime {
  private static readonly object Sync = new();
  private static bool initialized;

  private static readonly string[] AssemblyPatterns = {
    "HeuristicLab.*.dll", "HEAL.Attic.dll", "ALGLIB-*.dll", "AutoDiff-*.dll", "LibSVM-*.dll"
  };

  public static void Initialize() {
    lock (Sync) {
      if (initialized) return;
      var directory = AppContext.BaseDirectory;
      foreach (var path in AssemblyPatterns.SelectMany(p => Directory.EnumerateFiles(directory, p)).Distinct().Order(StringComparer.Ordinal)) {
        var name = AssemblyName.GetAssemblyName(path);
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => AssemblyName.ReferenceMatchesDefinition(a.GetName(), name))) continue;
        Assembly.Load(name);
      }
      ContentManager.Initialize(new PersistenceContentManager());
      initialized = true;
    }
  }
}
