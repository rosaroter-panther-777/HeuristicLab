#!/usr/bin/env bash
# Installs hl (command line) and HeuristicLab Studio for the current user.
#   next/tools/install.sh [--prefix DIR] [--uninstall]
# Default prefix: ~/.local -> launchers in ~/.local/bin (hl, hl-studio), programs in
# ~/.local/share/heuristiclab-next, desktop entry in ~/.local/share/applications.
# Framework-dependent Release builds: needs the .NET 10 runtime (dotnet on PATH or ~/.dotnet).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
prefix="$HOME/.local"
uninstall=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --prefix) prefix="$2"; shift 2 ;;
    --uninstall) uninstall=true; shift ;;
    -h|--help) sed -n '2,6p' "$0"; exit 0 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
prefix="$(mkdir -p "$prefix" && cd "$prefix" && pwd)"
app_dir="$prefix/share/heuristiclab-next"
bin_dir="$prefix/bin"
desktop_file="$prefix/share/applications/heuristiclab-studio.desktop"
marker="$app_dir/.heuristiclab-next-install"

if $uninstall; then
  rm -f "$bin_dir/hl" "$bin_dir/hl-studio" "$desktop_file"
  if [[ -f "$marker" ]]; then
    rm -r -- "$app_dir"
  elif [[ -e "$app_dir" ]]; then
    echo "not removing $app_dir: it was not created by this installer" >&2
  fi
  echo "Uninstalled from $prefix"
  exit 0
fi

dotnet_bin="$(command -v dotnet || true)"
[[ -z "$dotnet_bin" && -x "$HOME/.dotnet/dotnet" ]] && dotnet_bin="$HOME/.dotnet/dotnet"
[[ -z "$dotnet_bin" ]] && { echo "dotnet not found (install the .NET 10 SDK)" >&2; exit 1; }
dotnet_root="$(dirname "$(readlink -f "$dotnet_bin")")"

echo "Building Release into $app_dir ..."
mkdir -p "$app_dir" "$bin_dir" "$(dirname "$desktop_file")"
touch "$marker"
"$dotnet_bin" publish "$here/../app/HeuristicLab.Cli/HeuristicLab.Cli.csproj" -c Release -o "$app_dir/hl" --nologo -v quiet
"$dotnet_bin" publish "$here/../app/HeuristicLab.Studio/HeuristicLab.Studio.csproj" -c Release -o "$app_dir/studio" --nologo -v quiet

write_launcher() {  # name, dll
  cat > "$bin_dir/$1" <<LAUNCHER
#!/usr/bin/env bash
export DOTNET_ROOT="$dotnet_root"
exec "$dotnet_bin" "$2" "\$@"
LAUNCHER
  chmod +x "$bin_dir/$1"
}
write_launcher hl "$app_dir/hl/hl.dll"
write_launcher hl-studio "$app_dir/studio/HeuristicLab.Studio.dll"

cat > "$desktop_file" <<DESKTOP
[Desktop Entry]
Type=Application
Name=HeuristicLab Studio
Comment=Optimization and data analysis experiments
Exec=$bin_dir/hl-studio %f
Terminal=false
Categories=Science;Education;Development;
MimeType=application/x-heuristiclab;
DESKTOP

echo "Installed: $bin_dir/hl, $bin_dir/hl-studio, $desktop_file"
case ":$PATH:" in *":$bin_dir:"*) ;; *) echo "Note: $bin_dir is not on your PATH" ;; esac
