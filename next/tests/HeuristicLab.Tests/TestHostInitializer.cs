using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Tests {
  /// <summary>
  /// Replaces the legacy AssemblyInitializer (MSTest allows only one [AssemblyInitialize]):
  /// does what the legacy one did and adapts the test host to modern .NET on Linux.
  /// </summary>
  [TestClass]
  public static class TestHostInitializer {
    [AssemblyInitialize]
    public static void AssemblyInitialize(TestContext testContext) {
      AssertionsAsExceptions.Install();
      if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        LinkTestResourcesForBackslashPaths();

      // legacy AssemblyInitializer: load all assemblies, create output directories
      PluginLoader.Assemblies.Any();
      if (!Directory.Exists(SamplesUtils.SamplesDirectory)) Directory.CreateDirectory(SamplesUtils.SamplesDirectory);
      if (!Directory.Exists(ScriptingUtils.ScriptsDirectory)) Directory.CreateDirectory(ScriptingUtils.ScriptsDirectory);
    }

    /// <summary>
    /// Legacy tests open files as @"Test Resources\GA_TSP.hl". Off Windows the backslash is an
    /// ordinary file name character, so each resource also gets a symlink named literally that way.
    /// </summary>
    private static void LinkTestResourcesForBackslashPaths() {
      const string dir = "Test Resources";
      if (!Directory.Exists(dir)) return;
      foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) {
        var literalName = file.Replace('/', '\\');
        if (!File.Exists(literalName))
          File.CreateSymbolicLink(literalName, Path.GetFullPath(file));
      }
    }
  }
}
