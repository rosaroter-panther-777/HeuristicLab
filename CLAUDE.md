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
- next/app/HeuristicLab.Studio (Avalonia 12 MVVM, CommunityToolkit) on the runtime:
  New dialog (algorithm -> compatible problem -> benchmark instance or CSV/Parquet data, via
  Setups.Create like "hl new"), open/save .hl, editable parameters (text or operator choice,
  via ParameterEditor; invalid input reverted), run with seed or batches (runs, parallel,
  optional results folder) with live chart / per-run points and statistics, Results tab
  (results folder: runs + statistics grouped by any column). Own LineChart control.
  Tests (next/tests/HeuristicLab.Studio.Tests) run headless (Avalonia.Headless + Skia) and
  save screenshots (studio-*.png in the test bin folder) for visual review.
  Note: HeadlessUnitTestSession.Dispose() hangs (12.1.3) - never dispose it in tests.
- next/runtime/HeuristicLab.Next.Runtime (net10.0): the only way new front ends use the core.
  HlRuntime.Initialize, Catalog, Documents (.hl), Setups (shared "new" logic), ParameterEditor
  (set/describe), ProblemInstances, DataFiles/TabularData (CSV via HeuristicLab's parser with
  header-based separator check; Parquet via Parquet.Net 5.6.1 - 6.x dropped the column API),
  DataProblems (table -> regression/classification/time series/trading; kind decided by exactly
  consumed data types, never by contravariant pattern matching), Features (return, logreturn,
  diff, lag, lead, rolling mean/std/min/max, zscore; composable), OptimizerRunner, BatchRunner,
  ResultStore, ItemValues, Provenance. Front ends must not use WinForms-era APIs (ItemImage).
- next/app/HeuristicLab.Cli ("hl"), research workflow on the runtime:
  hl list algorithms|problems; hl instances <problem>;
  hl new <algorithm> [--problem P] [--instance I | --data f --target y] [--set N=V]... --out f.hl;
  hl data info f; hl data derive f --add EXPR... [--dropna] --out f2 (CSV or Parquet);
  hl new ... --data f --target y [--inputs a,lag(x,1..3)] [--training 66 --training-start 3];
  hl info f.hl; hl run f.hl [--seed S] [--set N=V]... [--repeat N --parallel K] [--timeout] [--out report.json]
  [--store DIR] [--save f.hl]; hl store list|summary [--metric M --by param:X]|export [--csv f] DIR.
  Exit 0 completed, 1 failed, 2 usage, 3 stopped (timeout/Ctrl+C; reports still written).
  Batches: explicit seeds S..S+N-1 (base seed drawn and recorded if not given), deep clones per
  run; parallel and sequential give identical results. Results store = folder of JSON reports.
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
- Autoregressive Modeling reads y[row - offset] without checking: set a training start
  (--training-start / DataProblems trainingStart) of at least its maximum time offset.
- Seeded runs are reproducible per runtime and platform, not across them: .NET Framework,
  .NET on Windows and .NET on Linux (glibc libm) can differ in the last bit of math results,
  and searches with near-ties amplify that into different but equally valid trajectories.
- Compile order matters: ports compile the legacy csproj's Compile items in their order
  (Directory.Build.targets). Type order drives plugin discovery and thus default operators;
  an alphabetical glob made PSO and GA grouping pick different defaults than legacy.

## Running tests
- next/tools/run-tests.sh [quick|daily|all]: quick skips the long Run.Daily category; quick and
  all also run next/tests/HeuristicLab.Next.Runtime.Tests (runtime + CLI).
- On Linux it excludes next/tests/known-failures-linux.txt (each entry with its reason:
  Windows-only GDI+/native DLL, Framework-specific expectations, or seeded Run.Daily
  results recorded on .NET Framework). Any other failure is a regression. Keep the list
  short and justified.
- Status 2026-10-09 on Linux/net10.0: quick 427/427, daily 75/75 (442 + 82 tests; 22
  excluded). Port fidelity verified on Windows (docs/windows-baseline.md): the ported
  libraries on .NET Framework 4.7.2 reproduce the recorded seeded sample results exactly;
  the remaining Run.Daily deviations are .NET runtime / platform libm differences.
- Fidelity check on Windows (opt-in net472 target of the tests):
  dotnet test next/tests/HeuristicLab.Tests -f net472 -p:HlFramework=true --filter <tests>

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
- Compiled sources are exactly the legacy csproj's Compile items, in their order (read by
  Directory.Build.targets). After every port run next/tools/check-linked-sources.py.
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
