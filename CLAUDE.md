# HeuristicLab Fork — Project Instructions

## Context
- Fork of HeuristicLab 3.3.16 (legacy net472, dormant upstream) for building a new
  Avalonia-based quantitative research environment.
- Legacy tree = reference oracle. NEVER modify legacy project files unless the task
  explicitly says so. New work lives in next/.

## Branches and safety
- main is the integration branch (the user merges feature branches via pull requests).
  Never commit to main directly; start feature branches from origin/main.
  (deepseek-development is historical: it predates the port.)
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
  Experiments tab (first): several experiments in one workspace, each a tree of building blocks
  (algorithm + its problem, batch run, time-limit run) over plain HeuristicLab objects via
  runtime ExperimentTree (a batch run with several children wraps them in an inner Experiment
  named "Batch content"; a time-limit run takes one algorithm). Block design (user's request,
  no rounded corners, no green/yellow): orange (#F7931D) square with the kind's pictogram, a dark
  square with the state (experiment: saved/unsaved; algorithm: status), the name; batch/time-limit
  runs end in an orange field (repetitions / time limit; while running "3 / 10" or "01:23 / 05:00").
  Pictograms are the user's SVGs in Assets/Pictograms (Controls.Pictogram reads the paths; the
  working status turns as its animateTransform says). Algorithm status from the algorithm's
  events: no problem, not run / stopped by the user, waiting (in a running experiment, paused),
  working, finished (also stopped by a time limit), failed. Unsaved = changed since opened/saved
  (BlockViewModel.MarkModified; detail edits via EditContext.BeginEdit; runs count as changes).
  Click: details/parameters, double-click: rename, right-click: menu. Start all / start selected (ticked) experiments, enabled only
  when complete; no seed is imposed (a fixed seed would make batch repetitions identical).
  Legacy algorithms crash on Problem = null (OnProblemChanged): clone problems instead of
  detaching them; "Remove problem" tolerates exactly that crash.
  Algorithm detail (click an algorithm or its problem): tabs Problem (library/instance, typed
  parameters, coordinates + best known tour), Algorithm, Results (live, tables as charts), Runs,
  Operator Graph (layered from the initial operator, nested graphs, breakpoints), Engine (choice,
  log); Start/Pause/Stop/Prepare. Parameter editing via runtime ItemInspector/CheckedList:
  bool, text, choices with nested parameters, arrays/matrices, checked lists (analyzers), Show in
  Run (GetsCollected). Views are found by ViewLocator (XyzViewModel -> XyzView), so nested
  editors recurse.
  Live monitoring: while anything runs (experiments or a single algorithm), a workspace timer
  (500 ms, ExperimentWorkspaceViewModel.RefreshLive) refreshes the shown detail: results in place
  (selection stays), the selected result's quality/visualization/value (tours via
  ItemInspector.TourOf, tables as charts, structured values via ItemInspector.Members), runs
  tables and tree counters. Reads tolerate the running algorithm changing the data.
  Visualizations (results and the Problem tab) come from runtime Visuals.Visualizations, a toolkit-
  free picture model mirroring HeuristicLab's default views: ChartVisual (DataTable chart types incl.
  histograms with HeuristicLab's binning, second y axis, scatter plots), SceneVisual (2D: tours, VRP
  routes, orienteering, QAP via multidimensional scaling, LAP, knapsack, 2D packing, Gantt charts,
  test function landscapes, expression trees, ant trail replayed with the ant interpreter, lawn),
  BoxesVisual (3D packing) and TextVisual (formulas, robot code). Studio renders them with LineChart,
  SceneView (zoom/pan) and BoxesView (software 3D, drag to rotate) in VisualsView; a picker chooses
  among several pictures and keeps the choice while results update. Orienteering has no crossover or
  mutation: run it with Variable Neighborhood Search.
  Samples (HeuristicLab's start page): runtime Samples embeds the 37 .hl files of
  HeuristicLab.Optimizer/3.3/Documents (linked) in the start page's groups; Studio lists them on the
  Experiments tab when nothing is selected ("Samples..."): algorithms open as experiments, scripts in
  the Scripts tab (code, run on a thread, output, compile errors, variables drawn like results; no
  kill - .NET has no thread abort). All run except SGP_Robocode (needs Robocode) and
  GUI_Automation_Script (WinForms MainForm, does not compile). Older .hl files lost the tree
  length analyzer's lookup on load (upstream #3139): fixed by a replacement
  SymbolicExpressionTreeLengthAnalyzer.cs in next/core (keeps the parameter's ActualName).
  Detailed analysis (algorithm detail tab): runtime Tracing.DetailedRun runs a copy of an operator-
  based algorithm on a RecordingEngine (sequential engine + a look at each operator's scope and its
  ancestors afterwards): every new or changed solution is a step (operator, parents, quality,
  iteration, time). Copies made by selection are recognized by content fingerprint (not new);
  crossover parents are the scope's sub-scopes. Iterations end at the algorithm's Analyzer (as
  HeuristicLab's quality charts index them), else at the Generations/Iterations counter. Raw
  solutions are drawn with their problem via Visualizations.ForSolution (decoders run in a scope
  holding the problem's parameters) and combined by Visuals.Composition: current solution red, what
  it has beyond its ancestors from before its iteration yellow, earlier iterations blue shades,
  later ones (finished runs) green shades, a selected range's start/end as wide/medium strokes
  under it; no transparency. Only shapes marked IsSolution are recolored (builders mark them;
  trees and the lawn are not layered). A selected range plays as an animation (iterations/s);
  "Earlier/Later iterations" inputs always apply (within a range too). Every chart, picture and 3D
  view has a clickable legend: series / shape groups (Shape.Group, else by kind) / items can be
  hidden; the hidden set can be shared via HiddenSet (VisualsViewModel.Hidden, analysis series).
  Fullscreen (button or F11; Esc or F11 leaves): AnalysisFullscreenWindow, picture fills the
  screen, AnalysisControlsView (navigation + chart) in a movable, resizable floating panel. Not for
  BasicAlgorithms or data analysis (GPR): no candidate solutions in scopes. HeuristicLab's GA
  default crossover for real vectors is CopyCrossover: only mutations create new solutions there.
  Results tab (RunAnalysisView, runtime Runs.*): ticked sources (any experiment / batch run /
  time-limit run / algorithm of the workspace, results folders) -> RunTable (source, origin
  relative to the source, run, param:*, result:*; numbers or text; a run from two sources once),
  filters (column op value), grouping by any combination of columns (numbers in N ranges),
  Table (varying parameters or all, sort, CSV export), Charts (box plot, scatter, histogram,
  cumulative distribution, curves of a DataTable row per run + group mean; NaN = line gap),
  Statistics (HeuristicLab.Analysis.Statistics: Kruskal-Wallis, Mann-Whitney U with
  Bonferroni-Holm, t-test, Cohen's d, Hedges' g), Run (one run's results with visualizations).
  Resources tab (runtime Resources/ResourceMonitor, remembered in settings.json): CPU cores =
  process affinity (hard limit, Linux/Windows), threads per algorithm (1 = HeuristicLab's default
  SequentialEngine; more = ParallelEngine with that DegreeOfParallelism, set by Resources.Configure
  before a start, only on prepared algorithms, Debug Engine kept), experiments at the same time
  (semaphore in StartAsync), memory limit (monitor stops running work), priority. Nothing in
  HeuristicLab uses the GPU. Runs get "Resources: ..." parameters (Resources.Tag) to compare in the
  Results tab. Same seed, same result on any threads: the parallel engine only parallelizes
  evaluation (tested).
  HeuristicLab facts behind it: only the Debug Engine honors breakpoints; Prepare() gives an
  algorithm a new Results collection (never cache it); an algorithm's valid operators (move
  generators, ...) come from its problem, so rebuild parameter views after a problem change.
  New dialog (algorithm -> compatible problem -> benchmark instance or CSV/Parquet data, via
  Setups.Create like "hl new"), open/save .hl, editable parameters (text or operator choice,
  via ParameterEditor; invalid input reverted), run with seed or batches (runs, parallel,
  optional results folder) with live chart / per-run points and statistics, Results tab
  (results folder: runs + statistics grouped by any column), Solution tab (model, metrics,
  charts), sweep / walk-forward section. Remembers recent files, seed/runs/parallel and the
  results folder (~/.config/HeuristicLab.Studio/settings.json). Own LineChart control.
  Install for the user: next/tools/install.sh [--prefix DIR] [--uninstall] (Release publish,
  launchers hl and hl-studio in ~/.local/bin, desktop entry; needs the .NET 10 runtime).
  DataFiles.Read/Write block on Parquet.Net's async API: keep that work on the thread pool
  (Task.Run), else it deadlocks on the UI thread.
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
  ResultStore (label:/param:/result: columns), ItemValues, Provenance, Solutions (models,
  metrics, predictions, trading equity, tours), WalkForward (rolling/expanding windows; also sets
  a symbolic problem's FitnessCalculationPartition), Sweeps (full factorial, common seeds, runs
  labelled "config"). Run labels (RunOptions.Labels) end up in reports and store rows.
  Front ends must not use WinForms-era APIs (ItemImage).
- next/app/HeuristicLab.Cli ("hl"), research workflow on the runtime:
  hl list algorithms|problems; hl instances <problem>;
  hl new <algorithm> [--problem P] [--instance I | --data f --target y] [--set N=V]... --out f.hl;
  hl data info f; hl data derive f --add EXPR... [--dropna] --out f2 (CSV or Parquet);
  hl new ... --data f --target y [--inputs a,lag(x,1..3)] [--training 66 --training-start 3];
  hl walkforward f.hl --train N --test M [--step --expanding --start --seed --parallel --store --out];
  hl show f.hl [--solution NAME] [--predictions out.csv] [--json];
  hl run f.hl --sweep "Name=v1,v2" [--sweep ...] --repeat N (full factorial, same seeds per config);
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
- Trading (symbolic): the target (price change) must be among the inputs, but the grammar only
  allows lagged variables with lag <= -1, so models cannot read the present (tested). Signals
  are path-dependent: HeuristicLab evaluates training and test each from a fresh start.
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
