#region License Information
/* HeuristicLab
 * Copyright (C) Heuristic and Evolutionary Algorithms Laboratory (HEAL)
 *
 * This file is part of HeuristicLab.
 *
 * HeuristicLab is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * HeuristicLab is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with HeuristicLab. If not, see <http://www.gnu.org/licenses/>.
 */
#endregion

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace HeuristicLab.Common.Compilation {
  /// <summary>
  /// Roslyn-based replacement for CSharpCodeProvider.CompileAssemblyFrom*, which throws
  /// PlatformNotSupportedException on .NET Core. Returns CodeDom CompilerResults so callers
  /// written against CodeDom keep working. Linked as source into the projects that need it.
  /// </summary>
  internal static class RoslynCompiler {
    public static CompilerResults Compile(string source, IEnumerable<string> referenceLocations, bool includeDebugInformation) {
      // an explicit encoding is required to emit debug information
      var syntaxTree = CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), path: "Script.cs");
      var compilation = CSharpCompilation.Create(
        "HeuristicLab.Compiled." + Guid.NewGuid().ToString("N"),
        new[] { syntaxTree },
        GetReferences(referenceLocations),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
          optimizationLevel: includeDebugInformation ? OptimizationLevel.Debug : OptimizationLevel.Release,
          warningLevel: 4));

      var results = new CompilerResults(new TempFileCollection());
      using (var peStream = new MemoryStream())
      using (var pdbStream = includeDebugInformation ? new MemoryStream() : null) {
        var emitResult = compilation.Emit(peStream, pdbStream);
        foreach (var diagnostic in emitResult.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning)) {
          var span = diagnostic.Location.GetLineSpan();
          results.Errors.Add(new CompilerError(span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            diagnostic.Id, diagnostic.GetMessage()) { IsWarning = diagnostic.Severity == DiagnosticSeverity.Warning });
        }
        results.NativeCompilerReturnValue = emitResult.Success ? 0 : 1;
        if (emitResult.Success)
          results.CompiledAssembly = pdbStream != null
            ? Assembly.Load(peStream.ToArray(), pdbStream.ToArray())
            : Assembly.Load(peStream.ToArray());
      }
      return results;
    }

    // The requested assemblies plus the runtime's trusted platform assemblies, which provide
    // the reference facades (System.Runtime, netstandard, ...) that compiled code binds against.
    private static IEnumerable<MetadataReference> GetReferences(IEnumerable<string> referenceLocations) {
      var platform = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
        .Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries);
      return referenceLocations.Concat(platform)
        .Where(l => !string.IsNullOrEmpty(l) && File.Exists(l))
        .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
        .Select(g => MetadataReference.CreateFromFile(g.First()));
    }
  }
}
