# Linux Build Feasibility Probe — 2026-10-07

## Verdict

The reusable computational core of HeuristicLab 3.3.16 compiles on Ubuntu Linux
with the .NET 8 SDK (8.0.425) against Mono 6.14's .NET Framework 4.7.2 reference
assemblies, with ZERO source-code changes. 46 assemblies built successfully into
a shared bin/ folder.

## Environment

- Ubuntu 26.04, Mono 6.14.1 (xbuild unused), .NET SDK 8.0.425 in ~/.dotnet
  (installed via dotnet-install.sh, no sudo, DOTNET_ROOT/PATH exported in ~/.bashrc)
- Probe ran in a throwaway detached git worktree at /tmp/hl-probe
- Real checkout was never modified (git status clean after the whole session)

## Projects built (all OK)

PluginInfrastructure, Common, Collections, Persistence, Core, Data, Parameters,
Operators, Random, SequentialEngine, ParallelEngine, Encodings.RealVectorEncoding,
Encodings.SymbolicExpressionTreeEncoding, Optimization, Optimization.Operators,
Algorithms.GeneticAlgorithm, Problems.DataAnalysis, Problems.DataAnalysis.Symbolic,
Problems.DataAnalysis.Symbolic.Regression, Problems.DataAnalysis.Symbolic.Classification,
Problems.DataAnalysis.Symbolic.TimeSeriesPrognosis, Algorithms.DataAnalysis,
Analysis, DataPreprocessing, plus ExtLibs: ALGLIB 3.7.0/3.17.0 (+wrappers),
AutoDiff 1.0 (+wrapper), LibSVM 3.12 (+wrapper), NativeInterpreter 0.2 wrapper.

## Build command pattern

dotnet build <project>.csproj \
  -p:FrameworkPathOverride=<mono 4.7.2-api with case-fix symlink> \
  -p:OutDir=<repo-root>/bin/

## Shims required (probe tree only)

1. Directory.Build.props at repo root:
   - <SolutionDir>$(MSBuildThisFileDirectory)</SolutionDir>  (csproj-only builds leave SolutionDir *Undefined*)
   - <GenerateResourceUsePreserializedResources>true</GenerateResourceUsePreserializedResources>  (modern resx pipeline)
   - Reference to System.Resources.Extensions.dll (NuGet 4.7.1, lib/net461)
2. Stub .nuget/NuGet.targets (empty <Project>) — 5 legacy projects import it and
   error if missing; original uses CodeTaskFactory, unsupported on Core MSBuild.
   Affected importers: 5 csproj files (Persistence among them).
3. Manual NuGet dlls into bin/: HEAL.Attic 1.5.0 (lib/net461),
   Google.Protobuf 3.6.1 (lib/net45), System.Resources.Extensions 4.7.1
4. Case-sensitivity fix: one project references "System.configuration" (lowercase);
   fixed via symlink System.configuration.dll -> System.Configuration.dll in a
   private copy of /usr/lib/mono/4.7.2-api.
5. PreBuildEvent.sh must be executable; it generates Plugin.cs and
   Properties/AssemblyInfo.cs from *.frame templates (version strings become 0
   without svnwcrev; harmless).

## Not proven yet (runtime risks)

- Nothing was executed, only compiled.
- Native dependencies are Windows-only at runtime: hl-native-interpreter.dll,
  glmnet-x64.dll, igraph, OR-Tools runtime.win-x64, Matlab COM, Scilab.
  Plan: put each behind an optional provider; use managed fallbacks.
- System.Drawing (Item.ItemImage) compiles fine but may fail at runtime outside
  Windows/.NET Framework; needs decoupling during the port (view-side icon registry).
- AppDomain/ReflectionOnlyLoad plugin infrastructure is .NET Framework-only;
  replace with LightweightApplicationManager-style assembly scanning.
- Tests: MSTest v1, no Linux runner; migrate to MSTest v2/xUnit during the port.

## Implications for the port

- Every blocker was 2010s build tooling, never code. Porting effort is mostly
  project-file modernization (SDK-style csproj, PackageReference) + the runtime
  decouplings listed above.
- Shared bin/ HintPath design means build order is inferred; Directory.Build.props
  is the single effective lever for probe-wide fixes.
- Symbolic regression (GP + AutoDiff parameter optimization) fully compiles —
  the flagship feature is portable.
