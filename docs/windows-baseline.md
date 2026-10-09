# Windows Baseline — 2026-10-08/09

Purpose: tell port defects apart from runtime/platform differences for the seeded
Run.Daily sample tests whose expected values were recorded with legacy HeuristicLab on
.NET Framework. Windows 11 VM, branch feature/next-scaffold.

## Results

| Test | Port on .NET Framework 4.7.2 (Win) | Port on .NET 10 (Win) | Port on .NET 10 (Linux) | Recorded (legacy, .NET Framework) |
|---|---|---|---|---|
| P3HIFF (found-on evaluation) | – | pass | 126082 | 89214 |
| Gaussian process regression sample | – | pass | −940.352 | −992.445 |
| GA grouping sample | pass | pass | pass | 127 |
| PSO Rastrigin sample | pass | pass | pass | 3.965 |
| GP symbolic regression with OS | pass | 0.99334 | 0.91224 | 0.99627 |
| Shape-constrained regression | pass | 0.45128 | 0.45128 | 0.03554 |
| Structure-template regression | pass | 4.24e-7 | 8.57e-5 | 5.03e-7 |

"–": not run on .NET Framework (already passing on Windows .NET 10).
GA grouping and PSO failed on .NET 10 before commit af8b06f3b (compile order fix) and pass
since; they are the control in the .NET Framework run.

## Conclusions

- The port is faithful: on .NET Framework the ported libraries reproduce all recorded values.
- One real port defect was found through this baseline and fixed: sources were compiled in
  alphabetical instead of legacy csproj order, which changed type order, plugin discovery
  order and therefore default operators (af8b06f3b).
- Remaining deviations are runtime differences: P3HIFF and the Gaussian process sample only
  on Linux (glibc libm), the other three on .NET 10 on both OSes (.NET Framework vs .NET).
  They are listed in next/tests/known-failures-linux.txt.

## How to reproduce (Windows, repo root)

    dotnet test next/tests/HeuristicLab.Tests/HeuristicLab.Tests.csproj -f net472 -p:HlFramework=true --filter "<names>"
    dotnet test next/tests/HeuristicLab.Tests/HeuristicLab.Tests.csproj --filter "<names>"
