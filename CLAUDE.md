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
- New code targets net10.0 (LTS). Legacy probe recipe: see docs/linux-build-probe.md

## Current state
- next/HeuristicLab.Next.slnx + next/app/HeuristicLab.Studio (Avalonia MVVM,
  CommunityToolkit, runs on this machine)
- Feasibility proven: 46 core assemblies compile with dotnet build +
  -p:FrameworkPathOverride=<mono 4.7.2-api with System.configuration case symlink>
- Port order follows the probe chain: Common → Collections → Persistence → Core →
  Data → Parameters/Operators → Random → Engines → Encodings → Optimization → ...

## Porting conventions
- Port via linked files: <Compile Include="..\..\..\LegacyPath\**\*.cs" /> — never
  copy files; legacy tree stays source of truth until a file needs changes.
- Keep original assembly names (HEAL.Attic .hl compatibility depends on it).
- Decouple as you go: Item.ItemImage → icon registry; AppDomain plugin loading →
  assembly scanning; native dlls → optional providers with managed fallbacks.
- One ported project = one commit, only with user approval.
