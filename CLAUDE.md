# HeuristicLab Fork — Project Instructions

## Context
- Fork of HeuristicLab 3.3.16 (legacy net472, dormant upstream) for building a new
  Avalonia-based quantitative research environment.
- Legacy tree = reference oracle. NEVER modify legacy project files unless the task
  explicitly says so. New work lives in next/.

## Branches and safety
- Never touch main. Work on feature branches off deepseek-development.
- Never push without the user's explicit approval. The user has authorized committing
  finished ports in next/ on feature branches without asking (one commit per project).
- No destructive git commands (reset --hard, clean -fd) or rm -rf, ever.
- Before suggesting any modification, run: git status --short, git diff --stat.
- Do not run legacy scripts: prepareProjectsForMono.sh, Build.cmd/ps1, Test.cmd/ps1,
  MergeConfigs.sh, PreBuildEvent.sh (outside builds), xbuild.

## Environment
- .NET SDKs 8.0.425 + 10.0.401 in ~/.dotnet (PATH/DOTNET_ROOT in ~/.bashrc)
- Mono 6.14.1 present; xbuild deprecated, do not use
- Ported libraries target netstandard2.0; apps and tests target net10.0 (LTS).
  Legacy probe recipe: see docs/linux-build-probe.md

## Current state
- next/HeuristicLab.Next.slnx + next/app/HeuristicLab.Studio (Avalonia MVVM,
  CommunityToolkit, runs on this machine)
- Feasibility proven: 46 core assemblies compile with dotnet build +
  -p:FrameworkPathOverride=<mono 4.7.2-api with System.configuration case symlink>
- Ported: every non-GUI project of the 3.3 solution (next/core, ~70 projects) plus the
  ExtLibs they need (next/extlibs: ALGLIB 3.17/3.7, LibSVM, AutoDiff, NativeInterpreter).
  Headless subsets: PluginInfrastructure, Visualization.ChartControlsExtensions (ChartUtil).
- Not ported yet: Problems.ExternalEvaluation* (protobuf 2.4 / Matlab COM / Scilab),
  ExactOptimization (OR-Tools, Windows-only native runtime), all Views/GUI, Hive/OKB/Services.
- next/tests/HeuristicLab.Tests: legacy tests linked unchanged incl. samples, run on
  net10.0 (MSTest 3.x). See "Running tests".

## Known runtime facts (verified on Linux / net10.0)
- HEAL.Attic is vendored (next/extlibs/HEAL.Attic, upstream v1.8 + patches in VENDORED.md):
  thread-safe caches/type registry. Same assembly identity as the NuGet package, same format.
- HEAL.Attic does not know .NET 5+'s StringEqualityComparer (default comparer of
  Dictionary<string,T>/HashSet<string>); next/core/HeuristicLab.Common/AtticRuntimeTypes.cs
  registers it in a module initializer. StringComparer.Ordinal(IgnoreCase) dictionaries
  still cannot be persisted on .NET Core (Attic re-creates comparers via Activator).
- Item.ItemImage and VSImageLibrary throw PlatformNotSupportedException off Windows
  (System.Drawing.Common). Front-end code must never touch ItemImage until decoupled.
  The CA1416 analyzer does NOT flag this path (Bitmaps come from ResourceManager).
  Decision (2026-10-07): decoupling deferred until a view needs per-type icons.
- A failed Debug.Assert/Contract.Assert kills the process on modern .NET (legacy suite ran
  Release). next/tests turns them into exceptions (AssertionsAsExceptions.cs).
- CSharpCodeProvider cannot compile on .NET Core. Scripting and Operators.Programmable use
  replacement files that compile with Roslyn (next/core/Shared/RoslynCompiler.cs).
- Library code calls ErrorHandling.ShowErrorDialog; the headless version forwards to the
  settable ErrorHandling.ErrorDisplay hook (front ends set it), else writes to Trace.
- Plugin type discovery (LightweightApplicationManager replacement) scans assemblies in
  name order: AppDomain order differs between runtimes, and defaults are picked from the
  first discovered type.
- Floating point: Math.Log/Exp/Tan come from the platform libm on Linux, so results can
  differ in the last bit from .NET Framework (and from .NET on Windows). Algorithms with
  near-ties (P3 linkage clustering, GP regression hyperparameter fits) may take a different
  but equally valid trajectory; seeded runs are reproducible per platform, not across.

## Running tests
- next/tools/run-tests.sh [quick|daily|all]: quick skips the long Run.Daily category.
- On Linux it excludes next/tests/known-failures-linux.txt (each entry with its reason:
  Windows-only GDI+/native DLL, or Framework-specific expectations). Any other failure
  is a regression. Keep the list short and justified.

## Porting conventions
- Generate projects with next/tools/port-project.py <legacy csproj>: it maps references to
  ported projects (unported ones are an error), resources, aliases, InternalsVisibleTo,
  packages, dead files, and reports what it drops. Hand-tune only what it reports.
- Port via linked files — never copy files; legacy tree stays source of truth until a
  file needs changes. next/core/Directory.Build.props/.targets do the linking: a project
  sets LegacyDir, AssemblyName, RootNamespace, AssemblyTitle, Description, and
  LegacyExclude ($(LegacyDir)-prefixed, semicolon-separated) for files to leave out.
- Package versions live only in next/core/Directory.Packages.props (central management
  with transitive pinning); PackageReference items carry no Version.
- Legacy csproj files list Compile items explicitly; the glob can pick up dead files.
  After every port run next/tools/check-linked-sources.py (fails on EXTRA files).
- After every port also run the platform check:
  dotnet build <project> -p:HlPlatformCheck=true  (net10.0, enables CA1416).
- Replacement files (when a legacy file needs changes) live in the next/ project folder,
  e.g. next/core/HeuristicLab.PluginInfrastructure/ErrorHandling.cs.
- Link a legacy test folder into next/tests as soon as all its dependencies are ported.
- Keep original assembly names (HEAL.Attic .hl compatibility depends on it).
- Decouple as you go: Item.ItemImage → icon registry; AppDomain plugin loading →
  assembly scanning; native dlls → optional providers with managed fallbacks.
- Compile against direct references only (DisableTransitiveProjectReferences), as the
  legacy build did.
- One ported project = one commit.
