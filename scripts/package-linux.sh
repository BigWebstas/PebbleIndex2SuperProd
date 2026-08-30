#!/usr/bin/env bash
# Publishes Index2SP for linux-x64 (self-contained + framework-dependent) and packs
# each as a .tar.gz in dist/. Requires the .NET 8 SDK.
#
#   scripts/package-linux.sh [version]
set -euo pipefail

version="${1:-1.0.0}"
rid="linux-x64"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$root/src/Index2SP/Index2SP.csproj"
dist="$root/dist"
mkdir -p "$dist"

pack() {
  local variant="$1" self_contained="$2" suffix="$3"
  local out="$root/artifacts/publish/linux-$variant"

  echo "==> publish linux $variant  (v$version / self-contained=$self_contained)"
  rm -rf "$out"
  dotnet publish "$project" -c Release -r "$rid" --self-contained "$self_contained" \
    -o "$out" "/p:Version=$version"

  [ -x "$out/Index2SP" ] || { echo "ERROR: $out/Index2SP missing"; exit 1; }
  chmod +x "$out/Index2SP"
  cp "$root/README.md" "$out/"
  cp "$root/config.example.json" "$out/"
  cp "$root/packaging/linux/index2sp.desktop" "$out/"
  cp "$root/packaging/linux/install.sh" "$root/packaging/linux/uninstall.sh" "$out/"
  chmod +x "$out/install.sh" "$out/uninstall.sh"

  local tarball="$dist/Index2SP-linux-x64${suffix}-${version}.tar.gz"
  rm -f "$tarball"
  tar -C "$out" -czf "$tarball" .
  echo "    -> $tarball"
}

pack "self-contained"      true  ""
pack "framework-dependent" false "-fd"

echo
ls -la "$dist"
