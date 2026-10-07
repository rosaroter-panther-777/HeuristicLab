#!/usr/bin/env python3
"""Compare each ported project's linked sources against its legacy csproj.

The ports link legacy sources with a **\\*.cs glob, but legacy csproj files list their
Compile items explicitly, so dead files on disk (not in the legacy csproj) would be picked
up silently. For every project under next/core and next/extlibs this reports:
  EXTRA    - compiled in the port but not listed in the legacy csproj (must be excluded)
  OMITTED  - listed in the legacy csproj but not compiled in the port (intentional
             exclusions or replaced files - review). Plugin.cs and Properties/AssemblyInfo.cs
             generated from .frame files are omitted by design and not reported.
Exit code is 1 if any EXTRA file is found.

Usage: next/tools/check-linked-sources.py [project-name-filter]
"""
import glob, json, os, re, subprocess, sys

next_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
name_filter = sys.argv[1] if len(sys.argv) > 1 else ""

def msbuild_query(csproj):
    out = subprocess.run(["dotnet", "msbuild", csproj, "-getProperty:LegacyDir", "-getItem:Compile"],
                         capture_output=True, text=True, check=True).stdout
    data = json.loads(out)
    legacy_dir = os.path.normpath(os.path.join(os.path.dirname(csproj), data["Properties"]["LegacyDir"].replace("\\", "/")))
    compiled = {os.path.normpath(i["FullPath"]) for i in data["Items"]["Compile"]}
    return legacy_dir, compiled

failed = False
for csproj in sorted(glob.glob(os.path.join(next_dir, "core", "*", "*.csproj")) +
                     glob.glob(os.path.join(next_dir, "extlibs", "*", "*.csproj"))):
    name = os.path.basename(csproj)[:-len(".csproj")]
    if name_filter not in name:
        continue
    legacy_dir, compiled = msbuild_query(csproj)
    legacy_projs = glob.glob(os.path.join(legacy_dir, "*.csproj"))
    if len(legacy_projs) != 1:
        print(f"{name}: expected one legacy csproj in {legacy_dir}, found {len(legacy_projs)}")
        failed = True
        continue
    text = open(legacy_projs[0], encoding="utf-8-sig").read()
    listed = {os.path.normpath(os.path.join(legacy_dir, p.replace("\\", "/")))
              for p in re.findall(r'<Compile Include="([^"]+)"', text)}
    compiled_legacy = {f for f in compiled if f.startswith(legacy_dir + os.sep)}
    own = sorted(os.path.relpath(f, os.path.dirname(csproj)) for f in compiled - compiled_legacy)
    extra = sorted(os.path.relpath(f, legacy_dir) for f in compiled_legacy - listed)
    generated = {f for f in ("Plugin.cs", os.path.join("Properties", "AssemblyInfo.cs"))
                 if os.path.exists(os.path.join(legacy_dir, f + ".frame"))}
    omitted = sorted(os.path.relpath(f, legacy_dir) for f in listed - compiled_legacy
                     if os.path.relpath(f, legacy_dir) not in generated)
    status = "FAIL" if extra else "ok"
    print(f"{status:4} {name}: {len(compiled_legacy)} linked, {len(own)} own"
          + (f", own: {', '.join(own)}" if own else ""))
    for f in extra:
        print(f"       EXTRA   {f}")
    for f in omitted:
        print(f"       OMITTED {f}")
    failed |= bool(extra)

sys.exit(1 if failed else 0)
