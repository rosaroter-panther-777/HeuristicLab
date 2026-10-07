#!/usr/bin/env python3
"""Generate an SDK-style next/ project for a legacy HeuristicLab project.

Reads the legacy csproj and its Properties/AssemblyInfo.cs.frame and writes
next/core/<Name>/<Name>.csproj (or next/extlibs/<AssemblyName>/... for ExtLibs), using
the linked-source settings from next/core/Directory.Build.*. Project references are mapped
to already ported next/ projects; references to unported projects are an error, so ports
happen in dependency order. Plugin wrapper projects in ExtLibs (only a Plugin.cs.frame)
are skipped as references. The project is added to next/HeuristicLab.Next.slnx.

Prints what it dropped or could not map (WinForms/WPF references, custom constants, ...)
so every port starts with a reviewable list instead of silent omissions.

Usage: next/tools/port-project.py <legacy csproj> [--force]
"""
import glob, os, re, sys
from xml.sax.saxutils import escape

repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
next_dir = os.path.join(repo, "next")

# Framework references that netstandard2.0 covers or that are dropped deliberately.
IGNORED_FRAMEWORK_REFS = {
    "System", "System.Core", "System.Data", "System.Data.DataSetExtensions", "System.Xml",
    "System.Xml.Linq", "System.Drawing", "System.Numerics",
    "System.Runtime.Serialization", "System.IO.Compression", "System.IO.Compression.FileSystem",
    "System.Net.Http", "System.ComponentModel.DataAnnotations",
}
# Framework references dropped with a warning: GUI or Windows-only stacks.
WARN_FRAMEWORK_REFS = {
    "System.Windows.Forms", "System.Windows.Forms.DataVisualization", "PresentationCore",
    "PresentationFramework", "WindowsBase", "System.Xaml", "System.ServiceModel", "System.Web",
    "System.Data.Linq", "System.Deployment", "System.Design",
}
# Framework references that map to NuGet packages (versions in Directory.Packages.props).
PACKAGE_FOR_FRAMEWORK_REF = {
    "System.configuration": "System.Configuration.ConfigurationManager",
    "System.Configuration": "System.Configuration.ConfigurationManager",
    "Microsoft.CSharp": "Microsoft.CSharp",  # runtime binder for 'dynamic'
}
# HEAL.Attic is vendored in next/extlibs and resolved as a ported project
PACKAGE_FOR_HINT = {"Google.Protobuf": "Google.Protobuf"}


def read(path):
    try:
        with open(path, encoding="utf-8-sig") as f:
            return f.read()
    except UnicodeDecodeError:  # some legacy sources are Windows-1252
        with open(path, encoding="latin-1") as f:
            return f.read()


def assembly_name(csproj_text):
    return re.search(r"<AssemblyName>([^<]+)</AssemblyName>", csproj_text).group(1)


def ported_projects():
    """AssemblyName -> next csproj path, for everything already ported."""
    result = {}
    for p in glob.glob(os.path.join(next_dir, "core", "*", "*.csproj")) + \
             glob.glob(os.path.join(next_dir, "extlibs", "*", "*.csproj")):
        m = re.search(r"<AssemblyName>([^<]+)</AssemblyName>", read(p))
        if m:
            result[m.group(1)] = p
    return result


def legacy_projects():
    """AssemblyName -> legacy csproj path, for the whole legacy tree."""
    result = {}
    for p in glob.glob(os.path.join(repo, "HeuristicLab*", "**", "*.csproj"), recursive=True):
        if os.sep + "next" + os.sep in p or os.sep + "obj" + os.sep in p:
            continue
        try:
            result[assembly_name(read(p))] = p
        except AttributeError:
            pass
    return result


def is_plugin_wrapper(csproj):
    """ExtLibs wrappers like HeuristicLab.ALGLIB-3.17.0 contain only plugin metadata."""
    d = os.path.dirname(csproj)
    sources = [f for f in glob.glob(os.path.join(d, "**", "*.cs"), recursive=True)
               if os.sep + "obj" + os.sep not in f and not f.endswith(("Plugin.cs", "AssemblyInfo.cs"))]
    return "HeuristicLab.ExtLibs" in csproj and not sources


def frame_attr(frame_text, attr):
    m = re.search(r'^\s*\[assembly:\s*%s\("([^"]*)"\)\]' % attr, frame_text, re.M)
    return m.group(1) if m else None


def manifest_name(root_ns, legacy_dir, rel_path, dependent_upon):
    """Manifest resource name as legacy MSBuild (CreateCSharpManifestResourceName) builds it."""
    rel = rel_path.replace("\\", "/")
    if rel.endswith(".resx") and dependent_upon:
        cs = read(os.path.join(legacy_dir, os.path.dirname(rel), dependent_upon))
        ns = re.search(r"^\s*namespace\s+([\w.]+)", cs, re.M)
        cls = re.search(r"\b(?:class|struct)\s+(\w+)", cs)
        if ns and cls:
            return f"{ns.group(1)}.{cls.group(1)}.resources"
    folders, filename = rel.split("/")[:-1], rel.split("/")[-1]
    # folder names are made identifier-safe, the file name is kept
    folders = [("_" if f[:1].isdigit() else "") + re.sub(r"[^\w]", "_", f) for f in folders]
    name = ".".join([root_ns] + folders + [filename])
    if name.endswith(".resx"):
        name = name[:-len(".resx")] + ".resources"
    return name


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    force = "--force" in sys.argv
    if len(args) != 1:
        sys.exit(__doc__)
    legacy_csproj = os.path.abspath(args[0])
    legacy_dir = os.path.dirname(legacy_csproj)
    text = read(legacy_csproj)
    asm = assembly_name(text)
    root_ns = re.search(r"<RootNamespace>([^<]+)</RootNamespace>", text).group(1)
    extlib = "HeuristicLab.ExtLibs" in legacy_csproj
    area = "extlibs" if extlib else "core"
    proj_name = asm if extlib else re.sub(r"-\d+(\.\d+)*$", "", asm)
    out_dir = os.path.join(next_dir, area, proj_name)
    out_csproj = os.path.join(out_dir, proj_name + ".csproj")
    if os.path.exists(out_csproj) and not force:
        sys.exit(f"{out_csproj} exists (use --force to overwrite)")

    notes, errors = [], []
    props = [("LegacyDir", os.path.relpath(legacy_dir, out_dir).replace("/", "\\") + "\\"),
             ("AssemblyName", asm), ("RootNamespace", root_ns)]

    frame = os.path.join(legacy_dir, "Properties", "AssemblyInfo.cs.frame")
    if os.path.exists(frame):
        ft = read(frame)
        for prop, attr in (("AssemblyTitle", "AssemblyTitle"), ("Description", "AssemblyDescription"),
                           ("AssemblyVersion", "AssemblyVersion")):
            v = frame_attr(ft, attr)
            if v:
                props.append((prop, v))
        friends = re.findall(r'^\s*\[assembly:\s*InternalsVisibleTo\("([^",]+)(?:,\s*PublicKey=([0-9a-fA-F]+))?"\)\]', ft, re.M)
    else:
        friends = []
        notes.append("no AssemblyInfo frame: legacy Properties/AssemblyInfo.cs is compiled as is")

    if not re.search(r"<SignAssembly>true</SignAssembly>", text):
        props.append(("SignAssembly", "false"))
    key = re.search(r"<AssemblyOriginatorKeyFile>([^<]+)</AssemblyOriginatorKeyFile>", text)
    if key and key.group(1) != "HeuristicLab.snk":
        props.append(("AssemblyOriginatorKeyFile", "$(LegacyDir)" + key.group(1)))
    if re.search(r"<AllowUnsafeBlocks>true</AllowUnsafeBlocks>", text):
        props.append(("AllowUnsafeBlocks", "true"))

    constants = set()
    for c in re.findall(r"<DefineConstants>([^<]*)</DefineConstants>", text):
        constants |= {x.strip() for x in c.split(";") if x.strip()}
    custom = sorted(constants - {"DEBUG", "TRACE"})
    if custom:
        notes.append("legacy defines custom constants (not carried over, review): " + ", ".join(custom))

    ported, legacy = ported_projects(), legacy_projects()
    project_refs, packages = set(), set()

    def add_assembly_ref(name, source, aliases=None):
        if name in PACKAGE_FOR_HINT:
            packages.add(PACKAGE_FOR_HINT[name])
        elif name in ported:
            ref = os.path.relpath(ported[name], out_dir).replace("/", "\\")
            # extern aliases (e.g. alglib_3_7 next to ALGLIB 3.17) are carried over as metadata
            project_refs.add(ref + (f'" Aliases="{aliases}' if aliases and aliases != "global" else ""))
        elif name in legacy and is_plugin_wrapper(legacy[name]):
            pass
        else:
            errors.append(f"{source} {name} is not ported yet")

    for ref in re.findall(r'<ProjectReference Include="([^"]+)"', text):
        ref_path = os.path.normpath(os.path.join(legacy_dir, ref.replace("\\", "/")))
        add_assembly_ref(assembly_name(read(ref_path)), "project reference")
    for m in re.finditer(r'<Reference Include="([^"]+)"\s*(/>|>(.*?)</Reference>)', text, re.S):
        ref, body = m.group(1).split(",")[0], m.group(3) or ""
        hint = re.search(r"<HintPath>([^<]+)</HintPath>", body)
        aliases = re.search(r"<Aliases>\s*([^<]+?)\s*</Aliases>", body)
        if hint:
            add_assembly_ref(os.path.splitext(os.path.basename(hint.group(1).replace("\\", "/")))[0], "binary reference",
                             aliases.group(1) if aliases else None)
            continue
        if ref in IGNORED_FRAMEWORK_REFS:
            continue
        if ref in PACKAGE_FOR_FRAMEWORK_REF:
            packages.add(PACKAGE_FOR_FRAMEWORK_REF[ref])
        elif ref in WARN_FRAMEWORK_REFS:
            notes.append(f"dropped framework reference {ref} (GUI/Windows-only)")
        elif ref not in PACKAGE_FOR_HINT:
            notes.append(f"dropped framework reference {ref} (review)")
    for pkg in re.findall(r'<PackageReference Include="([^"]+)"', text):
        packages.add(pkg)

    resources = []
    for m in re.finditer(r'<EmbeddedResource Include="([^"]+)"\s*(/>|>(.*?)</EmbeddedResource>)', text, re.S):
        rel, body = m.group(1), m.group(3) or ""
        dep = re.search(r"<DependentUpon>([^<]+)</DependentUpon>", body)
        logical = re.search(r"<LogicalName>([^<]+)</LogicalName>", body)
        name = logical.group(1) if logical else manifest_name(root_ns, legacy_dir, rel, dep.group(1) if dep else None)
        resources.append((rel, name))
    # resx files holding files or typed objects need preserialized resources on the SDK
    if any(r.endswith(".resx") and re.search(r'type="System\.(Resources\.ResXFileRef|Drawing)',
                                             read(os.path.join(legacy_dir, r.replace("\\", "/"))))
           for r, _ in resources):
        props.append(("GenerateResourceUsePreserializedResources", "true"))
        packages.add("System.Resources.Extensions")
    sources = [f for f in glob.glob(os.path.join(legacy_dir, "**", "*.cs"), recursive=True)
               if os.sep + "obj" + os.sep not in f]
    if any("System.Reflection.Emit" in read(f) for f in sources):
        packages.add("System.Reflection.Emit.Lightweight")
        packages.add("System.Reflection.Emit.ILGeneration")
    if any(re.search(r"CSharpCodeProvider|CompilerParameters|CompileAssemblyFrom", read(f)) for f in sources):
        packages.add("System.CodeDom")
        notes.append("uses CodeDom: CSharpCodeProvider compilation throws PlatformNotSupportedException on .NET Core")

    # dead files: on disk but not in the legacy csproj (the linked-source glob would pick them up)
    listed = {os.path.normpath(os.path.join(legacy_dir, c.replace("\\", "/")))
              for c in re.findall(r'<Compile Include="([^"]+)"', text)}
    dead = sorted(os.path.relpath(f, legacy_dir) for f in sources
                  if os.path.normpath(f) not in listed and os.sep + "bin" + os.sep not in f
                  and not os.path.exists(f + ".frame"))
    if dead:
        props.insert(3, ("LegacyExclude", ";".join("$(LegacyDir)" + d.replace("/", "\\") for d in dead)))
        notes.append("excluded dead files (on disk, not in legacy csproj): " + ", ".join(dead))

    copies = []
    for m in re.finditer(r'<(None|Content) Include="([^"]+)"\s*>(.*?)</\1>', text, re.S):
        if re.search(r"<CopyToOutputDirectory>(Always|PreserveNewest)", m.group(3)):
            copies.append(m.group(2))

    if errors:
        print(f"{asm}: cannot port yet:")
        for e in sorted(set(errors)):
            print("  " + e)
        sys.exit(1)

    lines = ['<Project Sdk="Microsoft.NET.Sdk">', "", "  <PropertyGroup>"]
    lines += [f"    <{k}>{escape(v)}</{k}>" for k, v in props]
    lines += ["  </PropertyGroup>"]
    if friends:
        lines += ["", "  <ItemGroup>"]
        lines += [f'    <InternalsVisibleTo Include="{n}"' + (f' Key="{k}"' if k else "") + " />" for n, k in friends]
        lines += ["  </ItemGroup>"]
    if resources:
        lines += ["", "  <ItemGroup>"]
        lines += [f'    <EmbeddedResource Include="$(LegacyDir){r}" LogicalName="{n}" />' for r, n in resources]
        lines += ["  </ItemGroup>"]
    if copies:
        lines += ["", "  <ItemGroup>"]
        lines += [f'    <None Include="$(LegacyDir){c}" Link="{os.path.basename(c.replace(chr(92), "/"))}" CopyToOutputDirectory="PreserveNewest" />'
                  for c in copies]
        lines += ["  </ItemGroup>"]
    if project_refs:
        lines += ["", "  <ItemGroup>"]
        lines += [f'    <ProjectReference Include="{r}" />' for r in sorted(project_refs)]
        lines += ["  </ItemGroup>"]
    if packages:
        lines += ["", "  <ItemGroup>"]
        lines += [f'    <PackageReference Include="{p}" />' for p in sorted(packages)]
        lines += ["  </ItemGroup>"]
    lines += ["", "</Project>", ""]
    os.makedirs(out_dir, exist_ok=True)
    with open(out_csproj, "w") as f:
        f.write("\n".join(lines))

    slnx = os.path.join(next_dir, "HeuristicLab.Next.slnx")
    s = read(slnx)
    entry = f'    <Project Path="{area}/{proj_name}/{proj_name}.csproj" />\n'
    if entry not in s:
        folder = f'  <Folder Name="/{area}/">\n'
        if folder not in s:
            s = s.replace("</Solution>", f"{folder}  </Folder>\n</Solution>")
        start = s.index(folder) + len(folder)
        end = s.index("  </Folder>", start)
        entries = sorted(set(s[start:end].splitlines(keepends=True)) | {entry})
        s = s[:start] + "".join(entries) + s[end:]
        with open(slnx, "w") as f:
            f.write(s)

    print(f"{asm}: wrote {os.path.relpath(out_csproj, repo)}")
    for n in notes:
        print("  note: " + n)
    for c in copies:
        print(f"  note: copies {c} to output (check for Windows-only native binaries)")


if __name__ == "__main__":
    main()
