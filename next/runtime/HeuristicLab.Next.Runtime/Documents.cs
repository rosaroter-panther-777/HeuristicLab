using HeuristicLab.Common;

namespace HeuristicLab.Next.Runtime;

/// <summary>Loading and saving HeuristicLab files (.hl, HEAL.Attic format; legacy XML is read too).</summary>
public static class Documents {
  public static IStorableContent Load(string path) {
    HlRuntime.Initialize();
    return ContentManager.Load(Path.GetFullPath(path));
  }

  public static void Save(IStorableContent content, string path, CancellationToken cancellationToken = default) {
    HlRuntime.Initialize();
    ContentManager.Save(content, Path.GetFullPath(path), compressed: true, cancellationToken);
  }
}
