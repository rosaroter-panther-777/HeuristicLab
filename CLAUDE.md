# HeuristicLab Fork — Project Instructions

## Context
- Fork of HeuristicLab 3.3.16 (legacy net472, dormant upstream) for building a new
  Avalonia-based quantitative research environment.
- Legacy tree = reference oracle. NEVER modify legacy project files unless the task
  explicitly says so. New work lives in next/.

## Branches and safety
- Never touch main. Work on feature branches off deepseek-development.
- Never commit or push without the user's explicit approval in this session.
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
- Ported (next/core): PluginInfrastructure (headless subset), Tracing, Common,
  Collections, Persistence, Common.Resources, Core, Data, Parameters, Operators, Random.
- next/tests/HeuristicLab.Tests: legacy tests linked unchanged, run on net10.0 (MSTest 3.x).
- Port order follows the probe chain: Common → Collections → Persistence → Core →
  Data → Parameters/Operators → Random → Engines → Encodings → Optimization → ...

## Known runtime facts (verified on Linux / net10.0)
- HEAL.Attic must be >= 1.8.0: 1.5.0 throws on the first (de)serialization on .NET 5+
  (registers removed CoreLib type LongEnumEqualityComparer`1). .hl round trip works on 1.8.0.
- Item.ItemImage and VSImageLibrary throw PlatformNotSupportedException off Windows
  (System.Drawing.Common). Front-end code must never touch ItemImage until decoupled.
  The CA1416 analyzer does NOT flag this path (Bitmaps come from ResourceManager).
- A failed Debug.Assert/Contract.Assert kills the process on modern .NET (legacy suite ran
  Release). next/tests turns them into exceptions (AssertionsAsExceptions.cs).

## Porting conventions
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
- One ported project = one commit, only with user approval.
